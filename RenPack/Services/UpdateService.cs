using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using NLog;

namespace RenPack.Services;

public sealed record UpdateCheckResult(bool UpdateAvailable, string CurrentVersion, string? LatestVersion, string? ReleaseUrl);

/// <summary>
/// Prüft GitHub-Releases auf neue Versionen (Kroste-Standard, proxy-aware, nicht blockierend).
/// Kein Self-Update-Zwang: meldet nur und verlinkt die Release-Seite.
/// </summary>
public sealed class UpdateService
{
    private const string Owner = "Kroste";
    private const string Repo = "RenPack";
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private UpdateCheckResult? _cached;

    public string CurrentVersion { get; } = ReadInformationalVersion();

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        Log.Info("Update-Check gegen {url}", url);

        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = WebRequest.DefaultWebProxy,
                DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"{Repo}-UpdateCheck");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var release = await http.GetFromJsonAsync<GithubRelease>(url, ct).ConfigureAwait(false);
            string? latestTag = release?.TagName?.TrimStart('v');
            string current = StripMetadata(CurrentVersion);

            bool available = latestTag is not null
                && Version.TryParse(StripMetadata(latestTag), out var latest)
                && Version.TryParse(current, out var cur)
                && latest > cur;

            _cached = new UpdateCheckResult(available, CurrentVersion, latestTag, release?.HtmlUrl);
            Log.Info("Update-Check fertig ({ms} ms): aktuell={cur}, neuste={latest}, verfügbar={avail}",
                sw.ElapsedMilliseconds, current, latestTag, available);
            return _cached;
        }
        catch (Exception ex)
        {
            // Offline/Proxy dürfen die App nicht stören — nur warnen.
            Log.Warn(ex, "Update-Check fehlgeschlagen");
            return new UpdateCheckResult(false, CurrentVersion, null, null);
        }
    }

    private static string StripMetadata(string v)
    {
        int plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        int dash = v.IndexOf('-');
        if (dash >= 0) v = v[..dash];
        return v;
    }

    private static string ReadInformationalVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "0.0.0";
    }

    private sealed class GithubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    }
}
