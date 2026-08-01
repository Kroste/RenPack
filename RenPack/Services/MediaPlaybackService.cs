using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using NLog;

namespace RenPack.Services;

/// <summary>
/// Kapselt LibVLC-Initialisierung + einen App-weit geteilten
/// <see cref="MediaPlayer"/> fuer die Preview-Pane. LibVLC-Init passiert
/// beim ersten Zugriff (lazy) — schlaegt fehl auf Systemen ohne libvlc
/// installiert (Linux ohne VLC-Paket). Dann bleibt <see cref="IsAvailable"/>
/// false und die UI zeigt statt Media-Playback einen Placeholder.
/// </summary>
public sealed class MediaPlaybackService : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private LibVLC? _lib;
    private MediaPlayer? _player;
    private bool _initialized;
    private bool _initFailed;
    private string? _initErrorMessage;

    public bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return !_initFailed;
        }
    }

    /// <summary>Bei Init-Fehler: kurze Diagnosemeldung fuer die UI
    /// (Placeholder-Text). Null wenn IsAvailable=true.</summary>
    public string? InitErrorMessage
    {
        get
        {
            EnsureInitialized();
            return _initErrorMessage;
        }
    }

    public MediaPlayer? Player
    {
        get
        {
            EnsureInitialized();
            return _player;
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;

        // Linux/macOS: **Wichtig**: LibVLCSharp's [DllImport("libvlc")]
        // sucht nur in .NET-Runtime-Pfaden (RID-native, App-Dir), NICHT
        // im System-Pfad — auch wenn vlc installiert ist. Deshalb muessen
        // wir libvlc.so.5 aus /usr/lib64 explicit via NativeLibrary.Load
        // in den Prozessraum ziehen, BEVOR Core.Initialize() P/Invokes
        // ausloest. Ist libvlc einmal geladen, findet der dynamische
        // Linker die Symbole via GOT/PLT.
        if (!OperatingSystem.IsWindows())
        {
            TryPreloadSystemLibVlc();
        }

        // Erst der einfache Weg — Core.Initialize() ohne Pfad.
        if (TryInit(pathHint: null)) return;

        // Fallback: bekannte Linux-Pfade explizit durchprobieren.
        foreach (var candidate in CandidatePaths())
        {
            if (Directory.Exists(candidate) && TryInit(candidate)) return;
        }

        _initFailed = true;
        Log.Warn("LibVLC nicht auffindbar. Versucht: [{paths}]",
            string.Join(", ", CandidatePaths()));
        _initErrorMessage ??= "libvlc.so nicht gefunden — Suchpfade in logs/RenPack.log.";
    }

    /// <summary>Sucht libvlc.so.5 (und libvlccore.so.9) in bekannten
    /// System-Pfaden und laedt sie via <see cref="NativeLibrary.Load(string)"/>
    /// in den Prozess. Setzt zusaetzlich <c>VLC_PLUGIN_PATH</c>, damit
    /// LibVLC seine Codec/Demux-Plugins findet — ohne die Plugins
    /// initialisiert LibVLC zwar, spielt aber nichts ab.</summary>
    private void TryPreloadSystemLibVlc()
    {
        // Reihenfolge: erst libvlccore (Abhaengigkeit), dann libvlc.
        var soCandidates = new[]
        {
            // Fedora/RHEL/Bazzite
            "/usr/lib64/libvlccore.so.9", "/usr/lib64/libvlc.so.5",
            // Debian/Ubuntu
            "/usr/lib/x86_64-linux-gnu/libvlccore.so.9",
            "/usr/lib/x86_64-linux-gnu/libvlc.so.5",
            // Arch, andere
            "/usr/lib/libvlccore.so.9", "/usr/lib/libvlc.so.5",
            // macOS Homebrew
            "/opt/homebrew/lib/libvlccore.dylib",
            "/opt/homebrew/lib/libvlc.dylib",
        };
        foreach (var so in soCandidates)
        {
            if (!File.Exists(so)) continue;
            try
            {
                NativeLibrary.Load(so);
                Log.Info("Preloaded {so}", so);
            }
            catch (Exception ex)
            {
                Log.Trace(ex, "NativeLibrary.Load fehlgeschlagen fuer {so}", so);
            }
        }

        // Plugin-Pfad setzen (nur wenn nicht bereits explizit vom Nutzer).
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VLC_PLUGIN_PATH")))
        {
            foreach (var pluginDir in new[]
            {
                "/usr/lib64/vlc/plugins",                    // Fedora/Bazzite
                "/usr/lib/x86_64-linux-gnu/vlc/plugins",     // Debian/Ubuntu
                "/usr/lib/vlc/plugins",                      // Arch, andere
                "/opt/homebrew/lib/vlc/plugins",             // macOS Homebrew
            })
            {
                if (Directory.Exists(pluginDir))
                {
                    Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", pluginDir);
                    Log.Info("VLC_PLUGIN_PATH gesetzt: {dir}", pluginDir);
                    break;
                }
            }
        }
    }

    private bool TryInit(string? pathHint)
    {
        try
        {
            if (pathHint is null) Core.Initialize();
            else Core.Initialize(pathHint);

            _lib = new LibVLC(enableDebugLogs: false);
            _player = new MediaPlayer(_lib);
            Log.Info("LibVLC initialisiert (Version {ver}, Hint={hint})",
                _lib.Version, pathHint ?? "<default>");
            _initErrorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            _initErrorMessage ??= ex.Message;
            Log.Trace(ex, "LibVLC-Init fehlgeschlagen (Hint={hint})",
                pathHint ?? "<default>");
            return false;
        }
    }

    /// <summary>Kandidaten fuer libvlc.so-Verzeichnisse auf Linux/macOS.</summary>
    private static IEnumerable<string> CandidatePaths()
    {
        if (OperatingSystem.IsWindows()) yield break;
        yield return "/usr/lib64";
        yield return "/usr/lib/x86_64-linux-gnu";
        yield return "/usr/lib";
        yield return "/usr/local/lib";
        yield return "/app/lib";
        yield return "/opt/homebrew/lib";
        yield return "/usr/local/opt/vlc/lib";
    }

    /// <summary>Startet die Wiedergabe einer Datei. Setzt eine bereits
    /// laufende Wiedergabe vorher.</summary>
    public void Play(string filePath)
    {
        EnsureInitialized();
        if (_lib is null || _player is null) return;
        Stop();
        var media = new Media(_lib, filePath, FromType.FromPath);
        _player.Media = media;
        _player.Play();
        media.Dispose();
    }

    public void Pause()
    {
        if (_player is not null && _player.CanPause) _player.Pause();
    }

    public void TogglePause()
    {
        if (_player is null) return;
        if (_player.IsPlaying) _player.Pause();
        else _player.Play();
    }

    public void Stop()
    {
        if (_player is null) return;
        try { _player.Stop(); } catch { }
    }

    public void Dispose()
    {
        try { _player?.Dispose(); } catch { }
        try { _lib?.Dispose(); } catch { }
        _player = null;
        _lib = null;
    }
}
