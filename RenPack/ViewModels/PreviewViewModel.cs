using System.Text;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;
using RenPack.Localization;
using RenPack.Services;

namespace RenPack.ViewModels;

/// <summary>
/// Vorschau-Panel neben der Dateiliste im MainWindow. Zeigt Text, Bild,
/// Video und Audio direkt an, damit man ohne Entpacken schnell reinschauen
/// bzw. reinhoeren kann. Unbekannte Formate: „Kein Preview" + Dateigroesse.
///
/// Limits: 512 KB fuer Text, 50 MB fuer Bilder, 500 MB fuer Video/Audio
/// (die groesseren Cutscenes in Ren'Py-Games sind selten drueber).
///
/// Video/Audio-Playback: LibVLC braucht einen Datei-Pfad, deshalb
/// extrahieren wir das Archiv-Entry ins Temp-Verzeichnis fuer die Dauer
/// der Preview. <see cref="Clear"/> raeumt die Temp-Datei wieder auf.
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
    private string? _currentTempPath;

    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string? _textContent;
    [ObservableProperty] private Bitmap? _imageContent;
    [ObservableProperty] private string? _placeholder;
    [ObservableProperty] private bool _hasContent;
    [ObservableProperty] private bool _isVideoPlaying;
    [ObservableProperty] private bool _isAudioActive;
    [ObservableProperty] private bool _isVideoActive;

    public bool IsText => TextContent is not null;
    public bool IsImage => ImageContent is not null;
    public bool IsPlaceholder => Placeholder is not null;
    public bool IsMedia => IsVideoActive || IsAudioActive;

    /// <summary>Der LibVLC-Player, damit die View direkt daran binden kann
    /// (VideoView.MediaPlayer). Kann null sein wenn LibVLC nicht verfuegbar.</summary>
    public LibVLCSharp.Shared.MediaPlayer? MediaPlayer => _media?.Player;

    partial void OnTextContentChanged(string? value) { OnPropertyChanged(nameof(IsText)); OnPropertyChanged(nameof(IsPlaceholder)); }
    partial void OnImageContentChanged(Bitmap? value) { OnPropertyChanged(nameof(IsImage)); OnPropertyChanged(nameof(IsPlaceholder)); }
    partial void OnPlaceholderChanged(string? value) => OnPropertyChanged(nameof(IsPlaceholder));
    partial void OnIsAudioActiveChanged(bool value) => OnPropertyChanged(nameof(IsMedia));
    partial void OnIsVideoActiveChanged(bool value) => OnPropertyChanged(nameof(IsMedia));

    public PreviewViewModel(IRenpyArchiveService archiveService, MediaPlaybackService? media = null)
    {
        _archiveService = archiveService;
        _media = media;
    }

    // Designer-ctor
    public PreviewViewModel() : this(new RenpyArchiveService(), null) { }

    /// <summary>Anzeige zuruecksetzen (kein Eintrag ausgewaehlt oder neu geladen).
    /// Stoppt eine laufende Wiedergabe und loescht die Temp-Datei.</summary>
    public void Clear()
    {
        try { _media?.Stop(); } catch { }
        TextContent = null;
        ImageContent = null;
        Placeholder = null;
        Headline = "";
        HasContent = false;
        IsAudioActive = false;
        IsVideoActive = false;
        IsVideoPlaying = false;
        CleanupTempFile();
    }

    private void CleanupTempFile()
    {
        if (_currentTempPath is null) return;
        try { if (File.Exists(_currentTempPath)) File.Delete(_currentTempPath); }
        catch { /* nicht kritisch */ }
        _currentTempPath = null;
    }

    /// <summary>Laedt den Preview fuer einen Archiv-Eintrag. Async, damit
    /// bei groesseren Dateien die UI nicht ruckelt.</summary>
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
        if (_media is null || !_media.IsAvailable)
        {
            // Bei Init-Fehler die konkrete Diagnose-Message anzeigen —
            // hilft massiv bei "libvlc.so nicht gefunden auf Bazzite/
            // Distrobox"-Faellen.
            var detail = _media?.InitErrorMessage;
            Placeholder = string.IsNullOrEmpty(detail)
                ? L.T("Preview_MediaUnavailable")
                : L.T("Preview_MediaUnavailable") + "\n\n" + detail;
            return;
        }

        // LibVLC will einen Pfad — Bytes ins Temp-Verzeichnis schreiben.
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
            return;
        }

        _currentTempPath = tmp;
        IsVideoActive = isVideo;
        IsAudioActive = !isVideo;
        _media.Play(tmp);
        IsVideoPlaying = true;
    }

    [RelayCommand]
    private void TogglePlay()
    {
        _media?.TogglePause();
        IsVideoPlaying = _media?.Player?.IsPlaying ?? false;
    }

    [RelayCommand]
    private void StopMedia()
    {
        _media?.Stop();
        IsVideoPlaying = false;
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
