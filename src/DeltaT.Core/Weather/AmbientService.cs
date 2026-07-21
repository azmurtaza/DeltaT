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

    /// <summary>Which reference the current <see cref="CurrentAmbientC"/> measures rise
    /// against: 0 = the outside weather (the original, default model), 1 = a user-set
    /// fixed indoor temperature. Telemetry tags every learned row with this so the two
    /// regimes' baselines never blend.</summary>
    int AmbientMode => 0;
}

public sealed record GeoLocation(double Latitude, double Longitude, string City, string Country, string Source)
{
    public string Display => string.IsNullOrWhiteSpace(Country) ? City : $"{City}, {Country}";
}

public sealed record AmbientReading(double OutsideC, DateTimeOffset FetchedUtc, GeoLocation Location);

/// <summary>Outside temperature, the honest way: the *location* is resolved once
/// (IP lookup or manual pick) and cached — it rarely changes. The *temperature*
/// is re-fetched on a user-tunable cadence (default 3 h) because weather moves
/// even when you don't. Offline,
/// the last reading survives restarts and is flagged stale rather than dropped.
/// APIs: Open-Meteo (weather + geocoding, keyless) and ipapi.co (one-shot locate).</summary>
public sealed class AmbientService : IAmbientProvider, IAsyncDisposable
{
    /// <summary>How often the outside temperature is re-fetched. User-tunable (Settings), because
    /// near the equator the temperature can swing hour to hour and a 3 h cadence lags it; default
    /// 3 h, clamped 1..6. Read fresh each refresh cycle, so a change applies from the next tick.</summary>
    private TimeSpan RefreshInterval =>
        TimeSpan.FromHours(Math.Clamp(_settings.GetInt(SettingsKeys.WeatherRefreshHours) ?? 3, 1, 6));

    /// <summary>A reading older than this is treated as unknown by the telemetry pipeline. Scales
    /// with the refresh cadence (1.5x), so a faster refresh also tightens what counts as stale.</summary>
    private TimeSpan StaleAfter => RefreshInterval * 1.5;

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

    /// <summary>The ambient the scoring pipeline measures rise against. In fixed-indoor mode
    /// this is the user's set temperature (a controlled room DeltaT trusts over the weather);
    /// otherwise it is the outside reading. The display offset (weather mode only) is applied
    /// at the display edge, not here.</summary>
    public double? CurrentAmbientC
    {
        get
        {
            if (FixedMode && FixedTempC is { } fixedC)
                return fixedC;
            lock (_gate) return _reading?.OutsideC;
        }
    }

    /// <summary>The raw outside reading, ignoring fixed-indoor mode. For the display and for
    /// anything that specifically wants the weather (the outside-temperature readout).</summary>
    public double? OutsideC
    {
        get { lock (_gate) return _reading?.OutsideC; }
    }

    /// <summary>User has chosen to score against a fixed indoor temperature instead of the
    /// outside weather.</summary>
    public bool FixedMode => _settings.GetBool(SettingsKeys.IndoorFixedMode, false);

    /// <summary>The user-set fixed indoor temperature (°C), when one has been entered.</summary>
    public double? FixedTempC => _settings.GetDouble(SettingsKeys.IndoorFixedTempC);

    public int AmbientMode => FixedMode ? 1 : 0;

    // A user-set indoor temperature is never "stale": it's a deliberate constant, not a fetched
    // reading that can age out. Only the weather path can go stale.
    public bool IsStale => !FixedMode && Current is { } r && DateTimeOffset.UtcNow - r.FetchedUtc > StaleAfter;

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

        StartRefreshLoop();
    }

    /// <summary>Starts the background refresh loop. Uses <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// rather than a fixed-period timer so <see cref="RefreshInterval"/> is re-read every cycle:
    /// a cadence change takes effect on the next tick, and <see cref="ApplyRefreshInterval"/> makes
    /// it immediate.</summary>
    private void StartRefreshLoop()
    {
        var cts = new CancellationTokenSource();
        _cts = cts;
        _loop = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(RefreshInterval, cts.Token).ConfigureAwait(false);
                    await RefreshAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Apply a just-changed refresh cadence now: restart the loop on the new interval and
    /// pull a fresh reading immediately, so the user sees the effect without waiting a whole cycle.
    /// The old loop observes cancellation and exits on its own.</summary>
    public void ApplyRefreshInterval()
    {
        if (_cts is null)
            return; // not started yet (StartAsync will pick up the new interval)
        CancellationTokenSource? old = _cts;
        StartRefreshLoop();
        old.Cancel();
        _ = RefreshAsync();
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
            return (ResolvePlaceName(root) ?? "Detected location", StringProp(root, "countryName") ?? "");
        }
        catch
        {
            return ("Detected location", "");
        }
    }

    /// <summary>Pick the most *recognizable* place name from a BigDataCloud reverse-geocode
    /// response. Its <c>city</c> field is often a tiny union-council/settlement — a clean
    /// 100 m WiFi fix in Citi Housing, Sialkot comes back "Daska Kalan", a village 8 km off
    /// that reads as "wrong city" even though the coordinate is good. The district-level
    /// administrative unit is almost always named for its principal city ("Sialkot District"
    /// → Sialkot), so prefer that. The weather is always fetched from the exact coordinate,
    /// so this only changes the label, never the reading. Falls back to the literal
    /// city/locality when no district-like unit is present. Public + pure for testing.</summary>
    public static string? ResolvePlaceName(JsonElement root)
    {
        if (root.TryGetProperty("localityInfo", out JsonElement info)
            && info.TryGetProperty("administrative", out JsonElement admin)
            && admin.ValueKind == JsonValueKind.Array)
        {
            // The principal populated unit between province and settlement: its description
            // reads district/county/prefecture/metropolitan. Prefer the most specific such
            // unit (highest order) and strip the administrative suffix off its name.
            string? best = null;
            int bestOrder = int.MinValue;
            foreach (JsonElement a in admin.EnumerateArray())
            {
                string desc = (StringProp(a, "description") ?? "").ToLowerInvariant();
                bool cityLike = desc.Contains("district") || desc.Contains("county")
                             || desc.Contains("prefecture") || desc.Contains("metropolitan");
                if (!cityLike || StringProp(a, "name") is not { Length: > 0 } name)
                    continue;
                int order = a.TryGetProperty("order", out JsonElement o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : 0;
                if (order >= bestOrder)
                {
                    best = CleanAdminName(name);
                    bestOrder = order;
                }
            }
            if (best is { Length: > 0 })
                return best;
        }
        return FirstNonEmpty(StringProp(root, "city"), StringProp(root, "locality"),
            StringProp(root, "principalSubdivision"));
    }

    private static string CleanAdminName(string name)
    {
        foreach (string suffix in new[]
                 { " District", " County", " Prefecture", " Metropolitan City", " Metropolitan Municipality" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name[..^suffix.Length].Trim();
        }
        return name.Trim();
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
