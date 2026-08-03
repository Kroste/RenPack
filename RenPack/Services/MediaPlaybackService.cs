using System.Diagnostics;
using System.Runtime.CompilerServices;
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

    /// <summary>Streamt Video-Frames als JPEG-Bytes fuer Inline-Preview.
    /// Nutzt ffmpeg mit <c>-f image2pipe -vcodec mjpeg</c> und liest den
    /// resultierenden MJPEG-Stream (JPEG-Frames back-to-back) frame-fuer-
    /// frame aus stdout. Framerate ist reduziert (default 12 fps) — reicht
    /// fuer Preview und schont GC (jeder Frame = Bitmap-Allocation).
    ///
    /// Verhalten bei Abbruch (Cancellation-Token): ffmpeg-Prozess wird
    /// killed, letzter yield-Frame beendet die Enumeration ordentlich.</summary>
    public async IAsyncEnumerable<byte[]> StreamFramesAsync(
        string videoPath, int fps = 12,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!HasFfmpeg || !File.Exists(videoPath)) yield break;
        if (fps < 1 || fps > 60) throw new ArgumentOutOfRangeException(nameof(fps));

        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(videoPath);
        // fps-Filter reduziert die Frame-Rate; vf muss vor dem Codec kommen.
        psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add($"fps={fps}");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("image2pipe");
        psi.ArgumentList.Add("-vcodec"); psi.ArgumentList.Add("mjpeg");
        psi.ArgumentList.Add("-q:v"); psi.ArgumentList.Add("6"); // 2 (best) .. 31 (worst)
        psi.ArgumentList.Add("-");   // stdout

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex) { Log.Warn(ex, "ffmpeg-Stream start fehlgeschlagen"); yield break; }
        if (proc is null) yield break;

        try
        {
            await foreach (var jpeg in JpegStreamReader.ReadAsync(proc.StandardOutput.BaseStream, ct))
                yield return jpeg;
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            try { proc.Dispose(); } catch { }
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

    /// <summary>Liest einen MJPEG-Stream (JPEG-Frames back-to-back) frame-
    /// fuer-frame. Nutzt SOI (0xFF 0xD8) / EOI (0xFF 0xD9)-Marker um Frame-
    /// Grenzen zu finden. Fuer die Preview reicht die naive Suche nach EOI —
    /// echte JPEG-Streams von ffmpeg's <c>-vcodec mjpeg</c> sind sauber
    /// getrennt.</summary>
    internal static class JpegStreamReader
    {
        private const byte Marker = 0xFF;
        private const byte Soi = 0xD8;  // Start Of Image (nach 0xFF)
        private const byte Eoi = 0xD9;  // End Of Image
        private const int ReadBufferSize = 64 * 1024;

        public static async IAsyncEnumerable<byte[]> ReadAsync(
            Stream input, [EnumeratorCancellation] CancellationToken ct)
        {
            // Kein Span across await — deshalb List<byte>.
            var buffer = new List<byte>(ReadBufferSize * 2);
            var read = new byte[ReadBufferSize];
            bool eofReached = false;

            while (true)
            {
                // 1. Alle vollstaendigen Frames aus dem Buffer extrahieren.
                while (TryExtractFrame(buffer, out var frame))
                    yield return frame;

                if (eofReached) yield break;

                // 2. Wenn nichts mehr zu extrahieren ist, mehr lesen.
                ct.ThrowIfCancellationRequested();
                int n;
                try { n = await input.ReadAsync(read.AsMemory(), ct); }
                catch (OperationCanceledException) { yield break; }
                if (n <= 0) { eofReached = true; continue; }

                for (int i = 0; i < n; i++) buffer.Add(read[i]);
            }
        }

        /// <summary>Versucht einen vollstaendigen JPEG-Frame aus dem Buffer
        /// zu extrahieren. Bei Erfolg wird der Frame und alles davor
        /// (Junk-Bytes vor SOI) aus dem Buffer entfernt und <c>true</c>
        /// zurueckgegeben.</summary>
        private static bool TryExtractFrame(List<byte> buffer, out byte[] frame)
        {
            frame = Array.Empty<byte>();
            int soiPos = -1;
            for (int i = 0; i < buffer.Count - 1; i++)
            {
                if (buffer[i] == Marker && buffer[i + 1] == Soi) { soiPos = i; break; }
            }
            if (soiPos < 0) return false;

            int eoiPos = -1;
            for (int i = soiPos + 2; i < buffer.Count - 1; i++)
            {
                if (buffer[i] == Marker && buffer[i + 1] == Eoi) { eoiPos = i; break; }
            }
            if (eoiPos < 0) return false;

            int frameLen = eoiPos + 2 - soiPos;
            frame = new byte[frameLen];
            buffer.CopyTo(soiPos, frame, 0, frameLen);
            buffer.RemoveRange(0, eoiPos + 2);
            return true;
        }
    }

    /// <summary>Suche nach ffmpeg. Erst <c>RENPACK_FFMPEG</c>-Env-Var,
    /// dann typische absolute Pfade, dann echter <c>PATH</c>-Scan mit
    /// Datei-Existenz-Check. Wenn nichts gefunden: <c>null</c> → das UI
    /// blendet Inline-Play aus. Frueherer Fallback „vertraue Process.Start
    /// mit relativem Namen" war unzuverlaessig (Windows macht kein
    /// PATH-Lookup mit <c>UseShellExecute=false</c>, auf Linux crasht
    /// <c>execvp</c> erst zur Laufzeit — statt ehrlich vorher zu sagen
    /// „ffmpeg fehlt").</summary>
    private static string? ResolveFfmpeg()
    {
        var explicitPath = Environment.GetEnvironmentVariable("RENPACK_FFMPEG");
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
            return explicitPath;

        bool isWindows = OperatingSystem.IsWindows();
        string exeName = isWindows ? "ffmpeg.exe" : "ffmpeg";

        var wellKnownPaths = isWindows
            ? Array.Empty<string>()
            : new[] { "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/opt/homebrew/bin/ffmpeg" };
        foreach (var p in wellKnownPaths)
            if (File.Exists(p)) return p;

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            string trimmed = dir.Trim('"'); // Windows-PATH kann Anfuehrungszeichen haben
            string full = Path.Combine(trimmed, exeName);
            if (File.Exists(full)) return full;
        }
        Log.Info("ffmpeg im PATH nicht gefunden — Inline-Preview deaktiviert. " +
            "Install: Linux 'sudo dnf install ffmpeg-free' (Fedora) / 'sudo apt install ffmpeg' (Debian), " +
            "Windows 'winget install ffmpeg', macOS 'brew install ffmpeg'.");
        return null;
    }
}
