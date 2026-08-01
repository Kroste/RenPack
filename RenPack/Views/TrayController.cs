using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NLog;
using RenPack.Localization;

namespace RenPack.Views;

/// <summary>
/// System-Tray nach Kroste-Standard.
/// - <b>Minimieren</b> → Fenster verschwindet in den Tray (<see cref="Window.Hide"/>).
/// - <b>Schließen ✕</b> → App beendet regulär.
/// - Klick aufs Tray-Icon oder Menü „Anzeigen" → Fenster kommt zurück.
///
/// Pflicht-Absicherungen (Skill: references/design.md → System-Tray):
/// GC-Referenz (App hält Instanz als Feld), Restore-Guard-Flag,
/// try/catch mit Fallback auf Standard-Minimieren (headless / kaputtes DBus).
/// </summary>
public sealed class TrayController
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    private readonly Application _app;
    private readonly Window _window;
    private TrayIcon? _tray;
    private bool _restoreInProgress;

    public TrayController(Application app, Window window)
    {
        _app = app;
        _window = window;
    }

    public void Install()
    {
        try
        {
            var iconUri = new Uri("avares://RenPack/Assets/RenPack.png");
            var icon = AssetLoader.Exists(iconUri)
                ? new WindowIcon(new Bitmap(AssetLoader.Open(iconUri)))
                : null;

            _tray = new TrayIcon
            {
                Icon = icon,
                ToolTipText = L.T("App_Title"),
                IsVisible = true,
                Menu = BuildMenu(),
            };
            _tray.Clicked += (_, _) => Restore();

            TrayIcon.SetIcons(_app, new TrayIcons { _tray });
            _window.PropertyChanged += OnWindowPropertyChanged;

            _logger.Info("System-Tray installiert (Minimize → Tray).");
        }
        catch (Exception ex)
        {
            _tray = null;
            _logger.Warn(ex, "System-Tray nicht verfügbar — Fallback: Standard-Minimieren.");
        }
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();

        var showItem = new NativeMenuItem(L.T("Tray_Show"));
        showItem.Click += (_, _) => Restore();
        menu.Add(showItem);

        menu.Add(new NativeMenuItemSeparator());

        var quitItem = new NativeMenuItem(L.T("Tray_Quit"));
        quitItem.Click += (_, _) => Quit();
        menu.Add(quitItem);

        return menu;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.WindowStateProperty) return;
        if (_restoreInProgress) return;
        if (_window.WindowState != WindowState.Minimized) return;

        _window.Hide();
    }

    /// <summary>Öffentlich, damit der Single-Instance-Guard bei einem
    /// Zweitstart die existierende Instance hochholen kann.</summary>
    public void Restore()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _restoreInProgress = true;
            try
            {
                _window.Show();
                _window.WindowState = WindowState.Normal;
                _window.Activate();
            }
            finally
            {
                _restoreInProgress = false;
            }
        });
    }

    private void Quit()
    {
        if (_app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
