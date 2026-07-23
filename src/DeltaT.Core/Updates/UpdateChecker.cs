using System.Net.Http;
using System.Text.Json;

namespace DeltaT.Core.Updates;

/// <summary>A published release found on GitHub that is newer than what's running.
/// <paramref name="Sha256"/> is the digest GitHub computed for the setup asset, lower-case
/// hex, or null on a release predating the API field. The updater refuses to run a download
/// that doesn't match it.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string DownloadUrl, string HtmlUrl, string Notes, string? Sha256 = null);

/// <summary>The setup asset on a release: where to get it, and what it must hash to.</summary>
internal sealed record InstallerAsset(string Url, string? Sha256);

/// <summary>Asks GitHub whether a newer DeltaT has shipped. The comparison logic is a
/// pure function (<see cref="ParseLatest"/>) so it's unit-testable without the network;
/// only <see cref="CheckAsync"/> touches the wire.</summary>
public sealed class UpdateChecker
{
    public const string Owner = "deltat-app";
    public const string Repo = "DeltaT";

    private readonly HttpClient _http;

    public UpdateChecker(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub's API rejects requests without a User-Agent.
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("DeltaT-Updater");
    }

    /// <summary>The latest release if it's newer than <paramref name="current"/> and
    /// carries a Windows setup asset; null otherwise (up to date, or nothing to apply).</summary>
    public async Task<ReleaseInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        string url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        string json = await _http.GetStringAsync(url, ct);
        return ParseLatest(json, current);
    }

    /// <summary>Pure: turn the GitHub <c>/releases/latest</c> payload into a
    /// <see cref="ReleaseInfo"/> when it names a higher version and ships an installer.
    /// Drafts and prereleases are ignored, so only stable builds ever auto-update.</summary>
    public static ReleaseInfo? ParseLatest(string json, Version current)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        if (IsTrue(root, "draft") || IsTrue(root, "prerelease"))
            return null;

        if (!root.TryGetProperty("tag_name", out JsonElement tagEl) || tagEl.GetString() is not { } tag)
            return null;
        if (ParseVersion(tag) is not { } version || version <= current)
            return null;

        InstallerAsset? asset = FindInstallerAsset(root);
        if (asset is null)
            return null; // a release with no setup .exe is nothing DeltaT can apply

        string html = root.TryGetProperty("html_url", out JsonElement h) ? h.GetString() ?? "" : "";
        string notes = root.TryGetProperty("body", out JsonElement b) ? b.GetString() ?? "" : "";
        return new ReleaseInfo(version, tag, asset.Url, html, notes, asset.Sha256);
    }

    /// <summary>"v1.2.3" or "1.2.3" → Version. A trailing label (e.g. "-beta") is dropped.</summary>
    public static Version? ParseVersion(string tag)
    {
        string s = tag.TrimStart('v', 'V').Trim();
        int dash = s.IndexOf('-');
        if (dash >= 0)
            s = s[..dash];
        return Version.TryParse(s, out Version? v) ? v : null;
    }

    private static InstallerAsset? FindInstallerAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        foreach (JsonElement a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
            if (name is null)
                continue;
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && name.Contains("Setup", StringComparison.OrdinalIgnoreCase)
                && a.TryGetProperty("browser_download_url", out JsonElement u)
                && u.GetString() is { } url)
                return new InstallerAsset(url, ParseDigest(a));
        }
        return null;
    }

    /// <summary>GitHub reports an asset digest as "sha256:&lt;hex&gt;". Returns the bare
    /// lower-case hex, or null if the field is absent (older releases) or not SHA-256.</summary>
    public static string? ParseDigest(JsonElement asset)
    {
        if (!asset.TryGetProperty("digest", out JsonElement d) || d.GetString() is not { } s)
            return null;
        const string prefix = "sha256:";
        if (!s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        string hex = s[prefix.Length..].Trim().ToLowerInvariant();
        if (hex.Length != 64 || !hex.All(Uri.IsHexDigit))
            return null;
        return hex;
    }

    private static bool IsTrue(JsonElement root, string prop) =>
        root.TryGetProperty(prop, out JsonElement e) && e.ValueKind == JsonValueKind.True;
}
