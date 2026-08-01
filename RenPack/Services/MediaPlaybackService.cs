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

        // Erst der einfache Weg — Core.Initialize() ohne Pfad, sucht libvlc
        // ueber dlopen (Linux/macOS) bzw. Windows-DLL-Search-Path.
        if (TryInit(pathHint: null)) return;

        // Fallback: bekannte Linux-Pfade explizit durchprobieren. Auf Fedora/
        // Bazzite ist libvlc.so unter /usr/lib64/, auf Debian/Ubuntu unter
        // /usr/lib/x86_64-linux-gnu/, auf Distros mit /opt-Layout woanders.
        // In Distrobox-Containern kann der Host-Pfad sichtbar sein wenn
        // der Container /usr/lib bind-mountet — sonst hilft nur "vlc"
        // im Container selbst installieren.
        foreach (var candidate in CandidatePaths())
        {
            if (Directory.Exists(candidate) && TryInit(candidate)) return;
        }

        _initFailed = true;
        Log.Warn("LibVLC nicht auffindbar. Versucht: [{paths}]",
            string.Join(", ", CandidatePaths()));
        _initErrorMessage ??= "libvlc.so nicht gefunden — Suchpfade in logs/RenPack.log.";
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
            // Erste Fehlermeldung merken — spaetere Fallback-Fehler
            // interessieren die UI nicht, aber die ursprueng-liche
            // Exception ist die diagnostisch wichtige.
            _initErrorMessage ??= ex.Message;
            Log.Trace(ex, "LibVLC-Init fehlgeschlagen (Hint={hint})",
                pathHint ?? "<default>");
            return false;
        }
    }

    /// <summary>Kandidaten fuer libvlc.so-Verzeichnisse auf Linux/macOS
    /// (Fedora/Bazzite, Debian/Ubuntu, Arch, macOS-Homebrew, NixOS).</summary>
    private static IEnumerable<string> CandidatePaths()
    {
        if (OperatingSystem.IsWindows()) yield break; // Windows: DLL-Search-Path
        yield return "/usr/lib64";                       // Fedora/RHEL/Bazzite
        yield return "/usr/lib/x86_64-linux-gnu";        // Debian/Ubuntu
        yield return "/usr/lib";                          // Arch, andere
        yield return "/usr/local/lib";                    // Manual installs
        yield return "/app/lib";                          // Flatpak-Kontext
        yield return "/opt/homebrew/lib";                 // macOS Apple Silicon
        yield return "/usr/local/opt/vlc/lib";            // macOS Intel Homebrew
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
