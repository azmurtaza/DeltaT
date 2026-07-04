using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Kelvin.Core.Storage;

namespace Kelvin.Core.Weather;

/// <summary>Anything that can say what the outside temperature is right now.</summary>
public interface IAmbientProvider
{
    double? CurrentAmbientC { get; }
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
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Kelvin/1.0");

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

    public async Task<GeoLocation?> TryAutoLocateAsync()
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                await _http.GetStringAsync("https://ipapi.co/json/").ConfigureAwait(false));
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("latitude", out JsonElement lat) || lat.ValueKind != JsonValueKind.Number)
                return null;
            return new GeoLocation(
                lat.GetDouble(),
                root.GetProperty("longitude").GetDouble(),
                root.TryGetProperty("city", out JsonElement c) ? c.GetString() ?? "Unknown" : "Unknown",
                root.TryGetProperty("country_name", out JsonElement n) ? n.GetString() ?? "" : "",
                "ip");
        }
        catch (Exception ex)
        {
            Error?.Invoke("auto-locate failed", ex);
            return null;
        }
    }

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
