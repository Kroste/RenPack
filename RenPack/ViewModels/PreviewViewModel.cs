using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;
using RenPack.Localization;
using RenPack.Services;

namespace RenPack.ViewModels;

/// <summary>
/// Vorschau-Panel neben der Dateiliste im MainWindow. Zeigt Text, Bild,
/// Video-Standbild und Audio-Placeholder. Fuer echte Wiedergabe kommt
/// der System-Default-Player (VLC/mpv/QuickTime/Windows Media Player)
/// per <c>Process.Start(useShellExecute)</c> zum Zug — kein
/// Inline-Media-Widget, kein LibVLC-Deployment-Chaos.
///
/// Fuer Video wird per ffmpeg das erste Frame gegrabbt und als Standbild
/// angezeigt; damit hat man sofort ein visuelles Feedback ohne die
/// ganze Datei zu extrahieren.
///
/// Limits: 512 KB Text, 50 MB Bilder, 500 MB Video/Audio.
/// </summary>
public sealed partial class PreviewViewModel : ObservableObject
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private const long TextMaxBytes = 512 * 1024;
    private const long ImageMaxBytes = 50L * 1024 * 1024;
    private const long MediaMaxBytes = 500L * 1024 * 1024;

    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".rpy", ".rpym", ".py", ".txt", ".json", ".xml", ".md", ".yaml", ".yml",
        ".toml", ".csv", ".log", ".ini", ".cfg", ".conf", ".js", ".ts", ".html",
        ".htm", ".css", ".sh", ".bat", ".ps1", ".sql", ".gitignore",
    };
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".ico",
    };
    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".webm", ".mp4", ".mkv", ".avi", ".mov", ".ogv", ".m4v",
    };
    private static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".ogg", ".oga", ".opus", ".wav", ".flac", ".m4a", ".aac",
    };

    private readonly IRenpyArchiveService _archiveService;
    private readonly MediaPlaybackService? _media;
    private string? _currentMediaTempPath;

    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string? _textContent;
    [ObservableProperty] private Bitmap? _imageContent;
    [ObservableProperty] private string? _placeholder;
    [ObservableProperty] private bool _hasContent;

    /// <summary>Video oder Audio erkannt — Extern-Player-Button anzeigen.</summary>
    [ObservableProperty] private bool _isMedia;
    [ObservableProperty] private bool _isAudioOnly;

    /// <summary>Inline-Playback ist verfuegbar (Video + ffmpeg installiert).
    /// Steuert Sichtbarkeit des ▶-Inline-Buttons. Bei Audio-Only oder
    /// fehlendem ffmpeg bleibt der User beim Extern-Player.</summary>
    [ObservableProperty] private bool _canInlinePlay;

    /// <summary>Inline-Playback laeuft gerade (ffmpeg-Frame-Stream).
    /// Steuert Button-Beschriftung ▶/⏸ und verhindert Doppel-Start.</summary>
    [ObservableProperty] private bool _isPlayingInline;
    private CancellationTokenSource? _inlinePlayCts;

    public bool IsText => TextContent is not null;
    public bool IsImage => ImageContent is not null;
    public bool IsPlaceholder => Placeholder is not null;

    partial void OnTextContentChanged(string? value) { OnPropertyChanged(nameof(IsText)); OnPropertyChanged(nameof(IsPlaceholder)); }
    partial void OnImageContentChanged(Bitmap? value) { OnPropertyChanged(nameof(IsImage)); OnPropertyChanged(nameof(IsPlaceholder)); }
    partial void OnPlaceholderChanged(string? value) => OnPropertyChanged(nameof(IsPlaceholder));

    public PreviewViewModel(IRenpyArchiveService archiveService, MediaPlaybackService? media = null)
    {
        _archiveService = archiveService;
        _media = media;
    }

    // Designer-ctor
    public PreviewViewModel() : this(new RenpyArchiveService(), null) { }

    public void Clear()
    {
        StopInlinePlayback();
        TextContent = null;
        ImageContent = null;
        Placeholder = null;
        Headline = "";
        HasContent = false;
        IsMedia = false;
        IsAudioOnly = false;
        CanInlinePlay = false;
        CleanupMediaTempFile();
    }

    private void StopInlinePlayback()
    {
        try { _inlinePlayCts?.Cancel(); } catch { }
        _inlinePlayCts?.Dispose();
        _inlinePlayCts = null;
        IsPlayingInline = false;
    }

    private void CleanupMediaTempFile()
    {
        if (_currentMediaTempPath is null) return;
        try { if (File.Exists(_currentMediaTempPath)) File.Delete(_currentMediaTempPath); }
        catch { }
        _currentMediaTempPath = null;
    }

    public async Task LoadAsync(string archivePath, RpaEntry entry)
    {
        Clear();
        HasContent = true;
        Headline = $"{entry.Path}  ·  {FormatSize(entry.Size)}";

        string ext = Path.GetExtension(entry.Path);
        bool isText = TextExts.Contains(ext);
        bool isImage = ImageExts.Contains(ext);
        bool isVideo = VideoExts.Contains(ext);
        bool isAudio = AudioExts.Contains(ext);

        if (!isText && !isImage && !isVideo && !isAudio)
        {
            Placeholder = L.F("Preview_UnsupportedFormat", ext.TrimStart('.').ToUpperInvariant());
            return;
        }

        long limit = isText ? TextMaxBytes : isImage ? ImageMaxBytes : MediaMaxBytes;
        if (entry.Size > limit)
        {
            Placeholder = L.F("Preview_TooLargeFormat", FormatSize(entry.Size), FormatSize(limit));
            return;
        }

        try
        {
            if (isText)
            {
                var bytes = await Task.Run(() => _archiveService.ReadEntryBytes(archivePath, entry, limit));
                if (bytes is null) { Placeholder = L.T("Preview_LoadFailed"); return; }
                TextContent = DecodeText(bytes);
            }
            else if (isImage)
            {
                var bytes = await Task.Run(() => _archiveService.ReadEntryBytes(archivePath, entry, limit));
                if (bytes is null) { Placeholder = L.T("Preview_LoadFailed"); return; }
                using var ms = new MemoryStream(bytes);
                ImageContent = new Bitmap(ms);
            }
            else // Video oder Audio
            {
                await LoadMediaAsync(archivePath, entry, isVideo);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Preview fehlgeschlagen fuer {path}", entry.Path);
            Placeholder = L.T("Preview_LoadFailed");
        }
    }

    private async Task LoadMediaAsync(string archivePath, RpaEntry entry, bool isVideo)
    {
        IsMedia = true;
        IsAudioOnly = !isVideo;
        CanInlinePlay = isVideo && _media is not null && _media.HasFfmpeg;

        // Die Datei ins Temp-Verzeichnis extrahieren — brauchen wir sowohl
        // fuer ffmpeg-Frame-Grab als auch fuer den externen Player.
        string tmp = Path.Combine(Path.GetTempPath(),
            $"renpack-media-{Guid.NewGuid():N}{Path.GetExtension(entry.Path)}");
        await Task.Run(() =>
        {
            var bytes = _archiveService.ReadEntryBytes(archivePath, entry, MediaMaxBytes);
            if (bytes is not null) File.WriteAllBytes(tmp, bytes);
        });
        if (!File.Exists(tmp))
        {
            Placeholder = L.T("Preview_LoadFailed");
            IsMedia = false;
            CanInlinePlay = false;
            return;
        }
        _currentMediaTempPath = tmp;

        // Video: erstes Frame per ffmpeg grabben und als Standbild anzeigen.
        // Wenn ffmpeg fehlt: kein Frame + kein Inline-Button, das Media-
        // Grid zeigt stattdessen den FfmpegMissing-Hint (siehe XAML).
        if (isVideo && CanInlinePlay)
        {
            var frameBytes = await _media!.GrabFirstFrameAsync(tmp);
            if (frameBytes is not null)
            {
                using var ms = new MemoryStream(frameBytes);
                ImageContent = new Bitmap(ms);
            }
        }
    }

    /// <summary>Oeffnet die aktuelle Media-Datei im System-Default-Player.
    /// Voraussetzung: die Datei wurde per <see cref="LoadAsync"/> in eine
    /// Temp-Datei gelegt.</summary>
    [RelayCommand]
    private void PlayExternal()
    {
        if (_media is null || _currentMediaTempPath is null) return;
        _media.OpenExternal(_currentMediaTempPath);
    }

    /// <summary>Toggle fuer Inline-Video-Playback via ffmpeg-Frame-Stream
    /// (kein Audio, 12fps Preview-Quali). Ersetzt das erste Frame durch
    /// eine Bitmap-Reihe im gleichen Image-Widget. Bei Doppelklick pausiert.</summary>
    [RelayCommand]
    private async Task ToggleInlinePlaybackAsync()
    {
        if (IsPlayingInline)
        {
            StopInlinePlayback();
            return;
        }
        if (_media is null || _currentMediaTempPath is null || !_media.HasFfmpeg
            || IsAudioOnly) return;

        _inlinePlayCts = new CancellationTokenSource();
        var ct = _inlinePlayCts.Token;
        IsPlayingInline = true;
        string path = _currentMediaTempPath;

        try
        {
            await foreach (var jpeg in _media.StreamFramesAsync(path, fps: 12, ct))
            {
                ct.ThrowIfCancellationRequested();
                Bitmap bmp;
                using (var ms = new MemoryStream(jpeg)) bmp = new Bitmap(ms);
                await Dispatcher.UIThread.InvokeAsync(() => ImageContent = bmp);
            }
        }
        catch (OperationCanceledException) { /* normal: Stop-Klick */ }
        catch (Exception ex)
        {
            Log.Warn(ex, "Inline-Playback fehlgeschlagen: {path}", path);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsPlayingInline = false);
        }
    }

    private static string DecodeText(byte[] bytes)
    {
        try
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:F1} MB";
        return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
    }
}
