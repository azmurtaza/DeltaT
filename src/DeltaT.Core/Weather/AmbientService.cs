using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using DeltaT.Core.Storage;

namespace DeltaT.Core.Weather;

/// <summary>Anything that can say what the outside temperature is right now.</summary>
public interface IAmbientProvider
{
    double? CurrentAmbientC { get; }

    /// <summary>True when the reading is too old to describe the weather outside right
    /// now (service unreachable for hours, offline start on a cached value). Consumers
    /// that LEARN from ambient (banding, deltas) must treat a stale reading as unknown —
    /// yesterday's 20° stamped onto today's heatwave poisons the baseline. Display may
    /// still show it, flagged.</summary>
    bool IsStale => false;
}

public sealed record GeoLocation(double Latitude, double Longitude, string City, string Country, string Source)
{
    public string Display => string.IsNullOrWhiteSpace(Country) ? City : $"{City}, {Country}";
}

public sealed record AmbientReading(double OutsideC, DateTimeOffset FetchedUtc, GeoLocation Location);

/// <summary>Outside temperature, the honest way: the *location* is resolved once
/// (IP lookup or manual pick) and cached — it rarely changes. The *temperature*
/// is re-fetched every 3 h because weather moves even when you don't. Offline,
/// the last reading survives restarts and is flagged stale rather than dropped.
/// APIs: Open-Meteo (weather + geocoding, keyless) and ipapi.co (one-shot locate).</summary>
public sealed class AmbientService : IAmbientProvider, IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(3);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(4.5);

    private readonly SettingsStore _settings;
    private readonly HttpClient _http;
    private readonly object _gate = new();

    private GeoLocation? _location;
    private AmbientReading? _reading;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action<AmbientReading?>? Updated;
    public event Action<string, Exception>? Error;

    public AmbientService(SettingsStore settings, HttpClient? http = null)
    {
        _settings = settings;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DeltaT/1.0");

        // Rehydrate what we knew last time so offline startups still have context.
        if (_settings.Get(SettingsKeys.LocationJson) is { } json)
        {
            try { _location = JsonSerializer.Deserialize<GeoLocation>(json); }
            catch { /* corrupted setting — re-locate */ }
        }
        if (_location is not null
            && _settings.GetDouble(SettingsKeys.LastAmbientC) is { } t
            && _settings.GetTimestamp(SettingsKeys.LastAmbientFetched) is { } ts)
        {
            _reading = new AmbientReading(t, ts, _location);
        }
    }

    public GeoLocation? Location
    {
        get { lock (_gate) return _location; }
    }

    public AmbientReading? Current
    {
        get { lock (_gate) return _reading; }
    }

    public double? CurrentAmbientC
    {
        get { lock (_gate) return _reading?.OutsideC; }
    }

    public bool IsStale => Current is { } r && DateTimeOffset.UtcNow - r.FetchedUtc > StaleAfter;

    /// <summary>Resolve location if needed, fetch once, then keep refreshing every 3 h.</summary>
    public async Task StartAsync()
    {
        if (Location is null)
        {
            GeoLocation? auto = await TryAutoLocateAsync().ConfigureAwait(false);
            if (auto is not null)
                SetLocation(auto, refresh: false);
        }
        await RefreshAsync().ConfigureAwait(false);

        _cts = new CancellationTokenSource();
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(RefreshInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
                    await RefreshAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        });
    }

    public void SetLocation(GeoLocation location, bool refresh = true)
    {
        lock (_gate) _location = location;
        _settings.Set(SettingsKeys.LocationJson, JsonSerializer.Serialize(location));
        if (refresh)
            _ = RefreshAsync();
    }

    /// <summary>Best location we can get without asking the user. Windows' own
    /// positioning comes first: it's WiFi/GNSS-based and lands in the right city,
    /// where IP geolocation regularly reports the ISP's hub a hundred kilometres away
    /// (a Sialkot connection "located" in Lahore shifts every weather reading). Only
    /// when Windows can't answer — location service off, no radios, older OS — does
    /// the IP-provider chain take over. Coarse is survivable for weather; wrong-city
    /// is not.</summary>
    public async Task<GeoLocation?> TryAutoLocateAsync()
    {
        if (await TryWindowsLocateAsync().ConfigureAwait(false) is { } precise)
            return precise;

        Exception? lastError = null;
        foreach ((string url, Func<JsonElement, GeoLocation?> parse) in LocationProviders)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(await _http.GetStringAsync(url).ConfigureAwait(false));
                if (parse(doc.RootElement) is { } location)
                    return location;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        if (lastError is not null)
            Error?.Invoke("auto-locate failed on all providers", lastError);
        return null;
    }

    /// <summary>Windows positioning (WinRT Geolocator). Returns null when the system
    /// location switch is off, access is denied, no position source exists, or the OS
    /// predates the API — callers then fall back to IP lookup. City-level accuracy is
    /// requested so laptops without GPS still resolve fast off WiFi.</summary>
    private async Task<GeoLocation?> TryWindowsLocateAsync()
    {
        try
        {
            var access = await Windows.Devices.Geolocation.Geolocator.RequestAccessAsync();
            if (access != Windows.Devices.Geolocation.GeolocationAccessStatus.Allowed)
                return null;

            var locator = new Windows.Devices.Geolocation.Geolocator { DesiredAccuracyInMeters = 1500 };
            var position = await locator.GetGeopositionAsync(
                    maximumAge: TimeSpan.FromHours(1), timeout: TimeSpan.FromSeconds(12))
                .AsTask().ConfigureAwait(false);

            var p = position.Coordinate.Point.Position;
            if (double.IsNaN(p.Latitude) || double.IsNaN(p.Longitude)
                || (Math.Abs(p.Latitude) < 0.0001 && Math.Abs(p.Longitude) < 0.0001))
                return null;

            (string city, string country) = await TryReverseGeocodeAsync(p.Latitude, p.Longitude).ConfigureAwait(false);
            return new GeoLocation(p.Latitude, p.Longitude, city, country, "windows");
        }
        catch
        {
            return null; // any failure here just hands over to the IP chain
        }
    }

    /// <summary>Coordinates → a human city name for the titlebar. Cosmetic only — the
    /// weather fetch runs on the coordinates either way — so failures degrade to a
    /// generic label rather than blocking the location.</summary>
    private async Task<(string City, string Country)> TryReverseGeocodeAsync(double lat, double lon)
    {
        try
        {
            string url = string.Create(CultureInfo.InvariantCulture,
                $"https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={lat:0.####}&longitude={lon:0.####}&localityLanguage=en");
            using JsonDocument doc = JsonDocument.Parse(await _http.GetStringAsync(url).ConfigureAwait(false));
            JsonElement root = doc.RootElement;
            string? city = FirstNonEmpty(StringProp(root, "city"), StringProp(root, "locality"),
                StringProp(root, "principalSubdivision"));
            string country = StringProp(root, "countryName") ?? "";
            return (city ?? "Detected location", country);
        }
        catch
        {
            return ("Detected location", "");
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static readonly (string Url, Func<JsonElement, GeoLocation?> Parse)[] LocationProviders =
    {
        ("https://ipwho.is/", ParseIpWhoIs),
        ("https://ipinfo.io/json", ParseIpInfo),
    };

    private static GeoLocation? ParseIpWhoIs(JsonElement root)
    {
        if (root.TryGetProperty("success", out JsonElement ok) && ok.ValueKind == JsonValueKind.False)
            return null;
        if (!root.TryGetProperty("latitude", out JsonElement lat) || lat.ValueKind != JsonValueKind.Number)
            return null;
        return new GeoLocation(lat.GetDouble(), root.GetProperty("longitude").GetDouble(),
            StringProp(root, "city") ?? "Unknown", StringProp(root, "country") ?? "", "ip");
    }

    private static GeoLocation? ParseIpInfo(JsonElement root)
    {
        if (StringProp(root, "loc") is not { } loc || loc.Split(',') is not { Length: 2 } parts
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
            return null;
        return new GeoLocation(lat, lon, StringProp(root, "city") ?? "Unknown", StringProp(root, "country") ?? "", "ip");
    }

    private static string? StringProp(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public async Task<IReadOnlyList<GeoLocation>> SearchCityAsync(string query, CancellationToken ct = default)
    {
        var results = new List<GeoLocation>();
        try
        {
            string url = "https://geocoding-api.open-meteo.com/v1/search?count=6&language=en&format=json&name="
                         + Uri.EscapeDataString(query);
            using JsonDocument doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("results", out JsonElement arr))
                return results;
            foreach (JsonElement item in arr.EnumerateArray())
            {
                string city = item.GetProperty("name").GetString() ?? "?";
                string country = item.TryGetProperty("country", out JsonElement co) ? co.GetString() ?? "" : "";
                if (item.TryGetProperty("admin1", out JsonElement a1) && a1.GetString() is { Length: > 0 } admin && admin != city)
                    city = $"{city} ({admin})";
                results.Add(new GeoLocation(
                    item.GetProperty("latitude").GetDouble(),
                    item.GetProperty("longitude").GetDouble(),
                    city, country, "manual"));
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke("city search failed", ex);
        }
        return results;
    }

    public async Task<bool> RefreshAsync()
    {
        GeoLocation? loc = Location;
        if (loc is null)
            return false;
        try
        {
            string url = string.Create(CultureInfo.InvariantCulture,
                $"https://api.open-meteo.com/v1/forecast?latitude={loc.Latitude:0.####}&longitude={loc.Longitude:0.####}&current=temperature_2m");
            using JsonDocument doc = JsonDocument.Parse(await _http.GetStringAsync(url).ConfigureAwait(false));
            double temp = doc.RootElement.GetProperty("current").GetProperty("temperature_2m").GetDouble();

            var reading = new AmbientReading(temp, DateTimeOffset.UtcNow, loc);
            lock (_gate) _reading = reading;
            _settings.SetDouble(SettingsKeys.LastAmbientC, temp);
            _settings.SetTimestamp(SettingsKeys.LastAmbientFetched, reading.FetchedUtc);
            Updated?.Invoke(reading);
            return true;
        }
        catch (Exception ex)
        {
            Error?.Invoke("weather refresh failed", ex);
            Updated?.Invoke(Current); // let the UI re-evaluate staleness
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            if (_loop is not null)
            {
                try { await _loop.ConfigureAwait(false); }
                catch { }
            }
            _cts.Dispose();
        }
        _http.Dispose();
    }
}
