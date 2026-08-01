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

    public bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return !_initFailed;
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
        try
        {
            Core.Initialize();
            _lib = new LibVLC(enableDebugLogs: false);
            _player = new MediaPlayer(_lib);
            Log.Info("LibVLC initialisiert (Version {ver})", _lib.Version);
        }
        catch (Exception ex)
        {
            _initFailed = true;
            Log.Warn(ex, "LibVLC nicht verfuegbar — Media-Preview deaktiviert. "
                + "Auf Linux: 'vlc' bzw. 'libvlc' installieren.");
        }
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
