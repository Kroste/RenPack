using System.Diagnostics;
using NLog;

namespace RenPack.Services;

/// <summary>
/// Media-Preview nach dem Kroste-Standard (Vorbild: Spektiv/FfmpegFrameGrabber).
/// <b>Kein LibVLCSharp</b> — das Deployment ist auf allen Nicht-Windows-
/// Plattformen fragil (P/Invoke findet libvlc.so nicht, AppImage-Squashfs
/// kennt keine Host-Libs, Distrobox-Container ist isoliert). Stattdessen:
///
/// <list type="bullet">
///   <item>Video-Standbild via <c>ffmpeg -frames:v 1</c> als JPEG-Bytes.</item>
///   <item>Playback via <c>Process.Start(useShellExecute)</c> → oeffnet die
///     Datei im System-Default-Player (VLC/mpv/QuickTime/Windows Media
///     Player). Kein Inline-Widget, kein Airspace-Problem, kein
///     Deployment-Chaos.</item>
/// </list>
///
/// Auf Linux liegt ffmpeg meist unter <c>/usr/bin/ffmpeg</c> (auf Bazzite via
/// <c>ffmpeg-free</c>-Paket). Auf Windows braucht der Nutzer ffmpeg im PATH
/// (winget, Chocolatey oder https://www.gyan.dev/ffmpeg/builds/).
/// </summary>
public sealed class MediaPlaybackService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public string? FfmpegPath { get; } = ResolveFfmpeg();
    public bool HasFfmpeg => !string.IsNullOrEmpty(FfmpegPath);

    /// <summary>Erstes Frame des Videos als JPEG-Bytes. Nutzt ffmpeg als
    /// Subprocess (kein Managed-Codec-Aufwand). Timeout 10s.</summary>
    public async Task<byte[]?> GrabFirstFrameAsync(string videoPath, CancellationToken ct = default)
    {
        if (!HasFfmpeg || !File.Exists(videoPath)) return null;

        string tmp = Path.Combine(Path.GetTempPath(), $"renpack-frame-{Guid.NewGuid():N}.jpg");
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath!,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(videoPath);
        psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-q:v"); psi.ArgumentList.Add("4");
        psi.ArgumentList.Add(tmp);

        try
        {
            using var proc = Process.Start(psi)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await proc.WaitForExitAsync(timeout.Token);

            if (proc.ExitCode == 0 && File.Exists(tmp))
                return await File.ReadAllBytesAsync(tmp, ct);
            Log.Debug("ffmpeg exit={code} fuer {path}", proc.ExitCode, videoPath);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "ffmpeg-Frame-Grab fehlgeschlagen: {path}", videoPath);
            return null;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>Oeffnet die Datei im System-Default-Player. Unter Linux
    /// nutzt <c>UseShellExecute=true</c> intern <c>xdg-open</c>, unter
    /// Windows die Datei-Assoziation, unter macOS <c>open</c>.</summary>
    public bool OpenExternal(string filePath)
    {
        if (!File.Exists(filePath)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            Log.Info("Media extern geoeffnet: {path}", filePath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Externes Oeffnen fehlgeschlagen: {path}", filePath);
            return false;
        }
    }

    /// <summary>Suche nach ffmpeg im PATH bzw. an typischen Stellen.
    /// Muster wie in Spektivs FfmpegFrameGrabber.</summary>
    private static string? ResolveFfmpeg()
    {
        var explicitPath = Environment.GetEnvironmentVariable("RENPACK_FFMPEG");
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
            return explicitPath;

        var candidates = OperatingSystem.IsWindows()
            ? new[] { "ffmpeg.exe" }
            : new[] { "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/opt/homebrew/bin/ffmpeg", "ffmpeg" };

        foreach (var c in candidates)
        {
            if (Path.IsPathRooted(c) && File.Exists(c)) return c;
            if (!Path.IsPathRooted(c)) return c; // vertraue PATH bei Process.Start
        }
        return null;
    }
}
