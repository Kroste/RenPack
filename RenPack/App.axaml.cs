using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using NLog;
using RenPack.Localization;
using RenPack.Services;
using RenPack.ViewModels;
using RenPack.Views;

namespace RenPack;

public partial class App : Application
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Globaler DI-Container. Auflösung erfolgt hier in App.axaml.cs (Kroste-Standard).</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>Vom Program.Main gesetzt, sobald der Guard erfolgreich
    /// die primaere Instanz reklamiert hat. Wir uebernehmen ihn hier,
    /// verkabeln den ActivationRequested-Event mit dem Tray-Restore und
    /// disposen ihn beim regulaeren App-Ende.</summary>
    public static SingleInstanceGuard? PendingGuard { get; set; }

    // GC-Referenz halten — sonst wird das Tray-Icon nach einer Weile eingesammelt.
    private TrayController? _tray;
    private SingleInstanceGuard? _guard;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Services = ConfigureServices();

        // UI-Sprache aus den persistierten Einstellungen aktivieren, bevor
        // das erste Fenster gebaut wird — sonst zeigt es kurz die
        // System-Sprache und flackert dann auf die gewaehlte um.
        var settings = Services.GetRequiredService<AiSettingsService>().Current;
        LocalizationService.Instance.SetCulture(settings.UiCulture);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            GlobalExceptionHandler.Attach(desktop);

            var mainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>(),
            };
            Services.GetRequiredService<WindowStateService>().Attach(mainWindow);
            desktop.MainWindow = mainWindow;
            Log.Info("Hauptfenster erstellt");

            // System-Tray nach MainWindow-Erzeugung (Kroste-Standard).
            _tray = new TrayController(this, mainWindow);
            _tray.Install();

            // Single-Instance-Guard uebernehmen: Zweitstart-Aktivierung
            // holt das Hauptfenster aus dem Tray zurueck.
            _guard = PendingGuard;
            PendingGuard = null;
            if (_guard is not null)
            {
                _guard.ActivationRequested += () =>
                    Dispatcher.UIThread.Post(() => _tray?.Restore());
            }
            desktop.Exit += (_, _) => _guard?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Services
        services.AddSingleton<IRenpyArchiveService, RenpyArchiveService>();
        services.AddSingleton<IRenpySaveService, RenpySaveService>();
        services.AddSingleton<RenpyRpycService>();
        services.AddSingleton<RenpyRpycDecompiler>();
        services.AddSingleton<RpycBatchService>();
        services.AddSingleton<UpdateService>();

        services.AddSingleton<WindowStateService>();
        services.AddSingleton<RecentFilesService>();
        services.AddSingleton<FavoriteVarsService>();
        services.AddSingleton<MediaPlaybackService>();

        // KI-Services (v0.4b — Multi-Provider)
        services.AddSingleton<AiSettingsService>();
        services.AddSingleton<AiProviderFactory>();
        services.AddSingleton<TranslationService>();
        services.AddHttpClient(); // AddSingleton<IHttpClientFactory>

        // ViewModels
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<SaveWindowViewModel>();
        services.AddTransient<SettingsWindowViewModel>();
        services.AddTransient<ModGeneratorViewModel>();
        // OllamaPullViewModel wird ad-hoc mit Modellnamen erzeugt, kein DI-Eintrag.

        return services.BuildServiceProvider();
    }
}
