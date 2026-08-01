using System.Diagnostics;
using Avalonia.Interactivity;
using NLog;
using RenPack.Localization;

namespace RenPack.Views;

public partial class LogViewerWindow : ChromeWindow
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private string _fullContent = "";
    private readonly string _logPath;

    public LogViewerWindow()
    {
        InitializeComponent();
        _logPath = ResolveLogPath();
        PathText.Text = _logPath;

        RefreshButton.Click += (_, _) => Reload();
        OpenExternalButton.Click += OnOpenExternal;
        CloseButton.Click += (_, _) => Close();
        FilterBox.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(FilterBox.Text)) ApplyFilter();
        };

        Opened += (_, _) => Reload();
    }

    private static string ResolveLogPath()
    {
        // NLog config: logs/RenPack.log relativ zum Working Directory.
        // Beim Publish landet das im App-Basisverzeichnis.
        var relative = Path.Combine("logs", "RenPack.log");
        var absolute = Path.Combine(AppContext.BaseDirectory, relative);
        return File.Exists(absolute) ? absolute : Path.GetFullPath(relative);
    }

    private void Reload()
    {
        try
        {
            if (!File.Exists(_logPath))
            {
                _fullContent = "";
                LogContent.Text = "";
                StatusText.Text = L.F("Log_NotFoundFormat", _logPath);
                return;
            }

            // Nur die letzten ~200 KB laden — bei langen Sessions kann
            // die Datei mehrere MB gross sein und das UI langsam werden.
            using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            const int limit = 200 * 1024;
            long skip = Math.Max(0, fs.Length - limit);
            fs.Seek(skip, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            _fullContent = reader.ReadToEnd();
            if (skip > 0) _fullContent = "…\n" + _fullContent;
            ApplyFilter();
            StatusText.Text = L.F("Log_SizeFormat", FormatSize(new FileInfo(_logPath).Length));

            // Nach unten scrollen — Nutzer will typischerweise die neueste Zeile sehen.
            LogScroll.ScrollToEnd();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Log-Viewer konnte die Datei nicht lesen");
            LogContent.Text = "";
            StatusText.Text = L.F("Log_ReadFailedFormat", ex.Message);
        }
    }

    private void ApplyFilter()
    {
        var q = FilterBox.Text?.Trim();
        if (string.IsNullOrEmpty(q))
        {
            LogContent.Text = _fullContent;
            return;
        }
        var lines = _fullContent.Split('\n')
            .Where(l => l.Contains(q, StringComparison.OrdinalIgnoreCase));
        LogContent.Text = string.Join('\n', lines);
    }

    private void OnOpenExternal(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Wir oeffnen den Ordner, nicht die Datei — Nutzer koennen dann
            // selbst waehlen (im Standard-Editor, im Terminal, …).
            var dir = Path.GetDirectoryName(_logPath);
            if (dir is null) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Log-Ordner konnte nicht extern geoeffnet werden");
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / 1024.0 / 1024:F1} MB";
    }
}
