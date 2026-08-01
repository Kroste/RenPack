using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NLog;
using RenPack.Localization;
using RenPack.Services;

namespace RenPack.Views;

public partial class AboutWindow : ChromeWindow
{
    private const string GithubUrl = "https://github.com/Kroste/RenPack";
    private const string BmcUrl = "https://buymeacoffee.com/kroste";
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly UpdateService? _updateService;
    private UpdateCheckResult? _lastCheck;

    // Parameterloser Ctor für den XAML-Designer.
    public AboutWindow()
    {
        InitializeComponent();
    }

    public AboutWindow(UpdateService updateService) : this()
    {
        _updateService = updateService;
        VersionText.Text = $"Version {updateService.CurrentVersion}";
        UpdateButton.Click += OnCheckUpdate;
        InstallUpdateButton.Click += OnInstallUpdate;
        GithubButton.Click += (_, _) => Launch(GithubUrl);
        BmcButton.Click += (_, _) => Launch(BmcUrl);
        LogButton.Click += async (_, _) => await new LogViewerWindow().ShowDialog(this);
    }

    private async void OnCheckUpdate(object? sender, RoutedEventArgs e)
    {
        if (_updateService is null) return;
        UpdateButton.IsEnabled = false;
        UpdateResult.Text = L.T("Update_Checking");
        try
        {
            _lastCheck = await _updateService.CheckForUpdateAsync();
            UpdateResult.Text = _lastCheck.UpdateAvailable
                ? L.F("Update_AvailableFormat", _lastCheck.LatestVersion ?? "?")
                : _lastCheck.LatestVersion is null
                    ? L.T("Update_NoAccess")
                    : L.T("Update_UpToDate");
            InstallUpdateButton.IsVisible = _lastCheck.UpdateAvailable && _lastCheck.AssetUrl is not null;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Update-Prüfung im Über-Fenster fehlgeschlagen");
            UpdateResult.Text = L.T("Update_CheckFailed");
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    private async void OnInstallUpdate(object? sender, RoutedEventArgs e)
    {
        if (_updateService is null || _lastCheck?.AssetUrl is null || _lastCheck.AssetName is null) return;

        bool ok = await MessageBox.ShowAsync(this,
            L.T("Update_ConfirmTitle"),
            L.F("Update_ConfirmBodyFormat", _lastCheck.LatestVersion ?? "?"),
            showCancel: true);
        if (!ok) return;

        InstallUpdateButton.IsEnabled = false;
        UpdateButton.IsEnabled = false;
        UpdateProgress.IsVisible = true;
        UpdateProgress.Value = 0;
        UpdateResult.Text = L.T("Update_Downloading");

        var destPath = Path.Combine(Path.GetTempPath(), _lastCheck.AssetName);
        var progress = new Progress<double>(p =>
            Dispatcher.UIThread.Post(() => UpdateProgress.Value = p));

        try
        {
            await _updateService.DownloadAssetAsync(_lastCheck.AssetUrl, destPath, progress);
            UpdateResult.Text = L.T("Update_Installing");
            _updateService.ApplyUpdateAndRestart(destPath);
            // Kurz warten, damit der Installer starten kann, dann App beenden —
            // der Installer wartet mit kill -0/Wait-Process aufs Prozessende.
            await Task.Delay(500);
            try { NLog.LogManager.Shutdown(); } catch { }
            Environment.Exit(0);
            // Brutaler Fallback: wenn Environment.Exit haengt (Finalizer,
            // Tray-Icon, Single-Instance-Guard-Thread), killt Process.Kill
            // den Prozess garantiert — der Installer wartet mit kill -0 nur
            // aufs Prozess-Ende, es muss nicht "sauber" sein.
            await Task.Delay(1500);
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Self-Update fehlgeschlagen");
            UpdateResult.Text = L.F("Update_InstallFailedFormat", ex.Message);
            InstallUpdateButton.IsEnabled = true;
            UpdateButton.IsEnabled = true;
        }
    }

    private void Launch(string url)
    {
        try
        {
            TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Link konnte nicht geöffnet werden: {url}", url);
        }
    }
}
