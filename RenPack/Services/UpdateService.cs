using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using NLog;

namespace RenPack.Services;

public sealed record UpdateCheckResult(
    bool UpdateAvailable,
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseUrl,
    string? AssetUrl,
    string? AssetName);

/// <summary>
/// Prüft GitHub-Releases auf neue Versionen und bietet Self-Update
/// (Kroste-Standard, proxy-aware, nicht blockierend, mit Nutzer-
/// Zustimmung). Selbst-Update-Muster: Download → Austausch-Skript →
/// alte Instanz beenden → neue Instanz starten.
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
            using var http = BuildHttpClient(timeoutSeconds: 15);
            var release = await http.GetFromJsonAsync<GithubRelease>(url, ct).ConfigureAwait(false);
            string? latestTag = release?.TagName?.TrimStart('v');
            string current = StripMetadata(CurrentVersion);

            bool available = latestTag is not null
                && Version.TryParse(StripMetadata(latestTag), out var latest)
                && Version.TryParse(current, out var cur)
                && latest > cur;

            var asset = release is not null ? SelectAsset(release) : null;
            _cached = new UpdateCheckResult(
                available, CurrentVersion, latestTag, release?.HtmlUrl,
                asset?.BrowserDownloadUrl, asset?.Name);
            Log.Info("Update-Check fertig ({ms} ms): aktuell={cur}, neuste={latest}, verfuegbar={avail}, asset={asset}",
                sw.ElapsedMilliseconds, current, latestTag, available, asset?.Name);
            return _cached;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Update-Check fehlgeschlagen");
            return new UpdateCheckResult(false, CurrentVersion, null, null, null, null);
        }
    }

    /// <summary>Laedt das Release-Asset in das Zielverzeichnis. Fortschritt
    /// via <paramref name="progress"/> als Bruchteil (0..1).</summary>
    public async Task<string> DownloadAssetAsync(string url, string destinationPath,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Log.Info("Update: Download {url} → {dest}", url, destinationPath);
        using var http = BuildHttpClient(timeoutSeconds: 300);
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{Repo}-Update");

        using var response = await http.GetAsync(url,
            HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(destinationPath);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (total is > 0) progress?.Report((double)done / total.Value);
        }
        progress?.Report(1.0);
        Log.Info("Update: Download fertig ({bytes} Bytes)", done);
        return destinationPath;
    }

    /// <summary>Ersetzt die laufende Installation durch die heruntergeladene
    /// Datei und startet die App neu. Beendet die alte Instanz — der Aufrufer
    /// sollte danach <see cref="Environment.Exit(int)"/> aufrufen.</summary>
    public void ApplyUpdateAndRestart(string downloadedAssetPath)
    {
        if (OperatingSystem.IsLinux()) ApplyUpdateLinux(downloadedAssetPath);
        else if (OperatingSystem.IsWindows()) ApplyUpdateWindows(downloadedAssetPath);
        else throw new PlatformNotSupportedException("Self-Update nur unter Windows/Linux.");
    }

    /// <summary>Linux: AppImage ersetzt sich selbst (cp -f, dann setsid).
    /// tar.gz wird ausgepackt nach BaseDirectory. Beide loggen in
    /// <c>logs/update.log</c>.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void ApplyUpdateLinux(string downloadedAssetPath)
    {
        string appImageEnv = Environment.GetEnvironmentVariable("APPIMAGE") ?? "";
        string ext = Path.GetExtension(downloadedAssetPath).ToLowerInvariant();
        string logPath = Path.Combine(AppContext.BaseDirectory, "logs", "update.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        string script;
        int pid = Environment.ProcessId;
        if (ext == ".appimage" && !string.IsNullOrEmpty(appImageEnv))
        {
            // Laufendes AppImage: das cp -f ueberschreibt die Datei, waehrend
            // sie noch als Loop-Device gemountet ist — Inode bleibt, das
            // gemountete FS bleibt gueltig bis zum Prozessende. Danach
            // setsid startet den Nachfolger unabhaengig vom sterbenden Script.
            script = $@"#!/usr/bin/env bash
exec >>{Escape(logPath)} 2>&1
echo ""[$(date -Iseconds)] update: warte auf pid {pid}""
while kill -0 {pid} 2>/dev/null; do sleep 0.3; done
echo ""[$(date -Iseconds)] update: ersetze {Escape(appImageEnv)}""
cp -f {Escape(downloadedAssetPath)} {Escape(appImageEnv)}
chmod +x {Escape(appImageEnv)}
rm -f {Escape(downloadedAssetPath)}
echo ""[$(date -Iseconds)] update: starte neu""
setsid {Escape(appImageEnv)} >/dev/null 2>&1 &
";
        }
        else if (ext is ".gz" or ".tgz")
        {
            string baseDir = AppContext.BaseDirectory.TrimEnd('/');
            script = $@"#!/usr/bin/env bash
exec >>{Escape(logPath)} 2>&1
echo ""[$(date -Iseconds)] update: warte auf pid {pid}""
while kill -0 {pid} 2>/dev/null; do sleep 0.3; done
echo ""[$(date -Iseconds)] update: entpacke nach {baseDir}""
tar -xzf {Escape(downloadedAssetPath)} -C {Escape(baseDir)}
chmod +x {Escape(Path.Combine(baseDir, "RenPack"))}
rm -f {Escape(downloadedAssetPath)}
echo ""[$(date -Iseconds)] update: starte neu""
setsid {Escape(Path.Combine(baseDir, "RenPack"))} >/dev/null 2>&1 &
";
        }
        else
        {
            throw new NotSupportedException($"Unbekanntes Linux-Asset: {downloadedAssetPath}");
        }

        string scriptPath = Path.Combine(Path.GetTempPath(), $"renpack-update-{pid}.sh");
        File.WriteAllText(scriptPath, script);
        File.SetUnixFileMode(scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Log.Info("Update: starte Installer-Skript {script}", scriptPath);
        var psi = new System.Diagnostics.ProcessStartInfo("/bin/bash", scriptPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        System.Diagnostics.Process.Start(psi);
    }

    /// <summary>Windows: ZIP daneben entpacken, .bat wartet auf Prozessende
    /// (powershell Wait-Process, statt tasklist-Polling), xcopy, Neustart.
    /// Batch-Zeilen OHNE fuehrende Einrueckung schreiben — sonst schluckt
    /// cmd goto :label.</summary>
    private static void ApplyUpdateWindows(string downloadedAssetPath)
    {
        int pid = Environment.ProcessId;
        string baseDir = AppContext.BaseDirectory.TrimEnd('\\');
        string workDir = Path.Combine(Path.GetTempPath(), $"renpack-update-{pid}");
        string extractDir = Path.Combine(workDir, "new");
        string logPath = Path.Combine(workDir, "update.log");
        Directory.CreateDirectory(extractDir);

        Log.Info("Update: entpacke ZIP {zip} → {dir}", downloadedAssetPath, extractDir);
        ZipFile.ExtractToDirectory(downloadedAssetPath, extractDir, overwriteFiles: true);

        // Wenn im ZIP ein einzelner Root-Ordner ist, davon aus arbeiten.
        var top = Directory.GetDirectories(extractDir);
        var srcRoot = top.Length == 1 && Directory.GetFiles(extractDir).Length == 0
            ? top[0] : extractDir;

        string batPath = Path.Combine(workDir, "install.bat");
        string exePath = Path.Combine(baseDir, "RenPack.exe");
        var batLines = new[]
        {
            "@echo off",
            $"echo [%DATE% %TIME%] update: warte auf pid {pid} >> \"{logPath}\"",
            $"powershell -NoProfile -Command \"Wait-Process -Id {pid} -ErrorAction SilentlyContinue\"",
            "timeout /t 1 /nobreak > nul",
            $"echo [%DATE% %TIME%] update: xcopy \"{srcRoot}\" -> \"{baseDir}\" >> \"{logPath}\"",
            $"xcopy /E /I /Y /Q \"{srcRoot}\\*\" \"{baseDir}\\\" >> \"{logPath}\" 2>&1",
            $"if errorlevel 1 goto :fail",
            $"del \"{downloadedAssetPath}\" >nul 2>&1",
            $"echo [%DATE% %TIME%] update: starte {exePath} >> \"{logPath}\"",
            $"start \"\" \"{exePath}\"",
            "exit /b 0",
            ":fail",
            $"echo [%DATE% %TIME%] update: fehlgeschlagen (errorlevel=%errorlevel%) >> \"{logPath}\"",
            "exit /b 1",
        };
        File.WriteAllLines(batPath, batLines);

        Log.Info("Update: starte Installer-Batch {bat}", batPath);
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c \"{batPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        System.Diagnostics.Process.Start(psi);
    }

    private static string Escape(string p) => "'" + p.Replace("'", "'\\''") + "'";

    /// <summary>Passendes Release-Asset fuer die laufende Plattform waehlen.
    /// Namensschema aus release.yml: <c>RenPack-X.Y.Z-{platform}</c>.</summary>
    private static GithubAsset? SelectAsset(GithubRelease release)
    {
        if (release.Assets is null || release.Assets.Count == 0) return null;
        if (OperatingSystem.IsWindows())
            return release.Assets.FirstOrDefault(a =>
                a.Name?.Contains("win-x64", StringComparison.OrdinalIgnoreCase) == true
                && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (OperatingSystem.IsLinux())
        {
            // AppImage bevorzugen (self-updatend), tar.gz als Fallback.
            return release.Assets.FirstOrDefault(a =>
                    a.Name?.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase) == true)
                ?? release.Assets.FirstOrDefault(a =>
                    a.Name?.Contains("linux-x64", StringComparison.OrdinalIgnoreCase) == true
                    && a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    private static HttpClient BuildHttpClient(int timeoutSeconds)
    {
        var handler = new HttpClientHandler
        {
            Proxy = WebRequest.DefaultWebProxy,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{Repo}-UpdateCheck");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
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
        [JsonPropertyName("assets")] public List<GithubAsset>? Assets { get; set; }
    }

    private sealed class GithubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}
