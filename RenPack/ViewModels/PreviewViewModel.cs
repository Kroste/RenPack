using System.Text;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using NLog;
using RenPack.Localization;
using RenPack.Services;

namespace RenPack.ViewModels;

/// <summary>
/// Vorschau-Panel neben der Dateiliste im MainWindow. Zeigt Text- und
/// Bilddateien direkt an, damit man ohne Entpacken schnell reinschauen
/// kann. Alles andere: Kurzbeschreibung „Kein Preview" + Dateigroesse.
///
/// Limits: 512 KB fuer Text (die meisten Ren'Py-Skripte sind kleiner),
/// 50 MB fuer Bilder. Ueber Limit → "Datei zu gross fuer Vorschau".
/// </summary>
public sealed partial class PreviewViewModel : ObservableObject
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private const long TextMaxBytes = 512 * 1024;
    private const long ImageMaxBytes = 50L * 1024 * 1024;

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

    private readonly IRenpyArchiveService _archiveService;

    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string? _textContent;
    [ObservableProperty] private Bitmap? _imageContent;
    [ObservableProperty] private string? _placeholder;
    [ObservableProperty] private bool _hasContent;

    public bool IsText => TextContent is not null;
    public bool IsImage => ImageContent is not null;
    public bool IsPlaceholder => Placeholder is not null;

    partial void OnTextContentChanged(string? value) { OnPropertyChanged(nameof(IsText)); OnPropertyChanged(nameof(IsPlaceholder)); }
    partial void OnImageContentChanged(Bitmap? value) { OnPropertyChanged(nameof(IsImage)); OnPropertyChanged(nameof(IsPlaceholder)); }
    partial void OnPlaceholderChanged(string? value) => OnPropertyChanged(nameof(IsPlaceholder));

    public PreviewViewModel(IRenpyArchiveService archiveService)
    {
        _archiveService = archiveService;
    }

    // Designer-ctor
    public PreviewViewModel() : this(new RenpyArchiveService()) { }

    /// <summary>Anzeige zuruecksetzen (wenn kein Eintrag ausgewaehlt ist).</summary>
    public void Clear()
    {
        TextContent = null;
        ImageContent = null;
        Placeholder = null;
        Headline = "";
        HasContent = false;
    }

    /// <summary>Laedt den Preview fuer einen Archiv-Eintrag. Async, damit
    /// bei groesseren Bildern die UI nicht ruckelt.</summary>
    public async Task LoadAsync(string archivePath, RpaEntry entry)
    {
        Clear();
        HasContent = true;
        Headline = $"{entry.Path}  ·  {FormatSize(entry.Size)}";

        string ext = Path.GetExtension(entry.Path);
        bool isText = TextExts.Contains(ext);
        bool isImage = ImageExts.Contains(ext);

        if (!isText && !isImage)
        {
            Placeholder = L.F("Preview_UnsupportedFormat", ext.TrimStart('.').ToUpperInvariant());
            return;
        }

        long limit = isText ? TextMaxBytes : ImageMaxBytes;
        if (entry.Size > limit)
        {
            Placeholder = L.F("Preview_TooLargeFormat", FormatSize(entry.Size), FormatSize(limit));
            return;
        }

        try
        {
            var bytes = await Task.Run(() => _archiveService.ReadEntryBytes(archivePath, entry, limit));
            if (bytes is null)
            {
                Placeholder = L.T("Preview_LoadFailed");
                return;
            }

            if (isText)
            {
                TextContent = DecodeText(bytes);
            }
            else
            {
                using var ms = new MemoryStream(bytes);
                ImageContent = new Bitmap(ms);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Preview fehlgeschlagen fuer {path}", entry.Path);
            Placeholder = L.T("Preview_LoadFailed");
        }
    }

    /// <summary>UTF-8 mit BOM-Erkennung, Fallback Latin-1.</summary>
    private static string DecodeText(byte[] bytes)
    {
        try
        {
            // BOM-Check
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
