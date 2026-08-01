using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>(),
            };
            Log.Info("Hauptfenster erstellt");
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

        // KI-Services (v0.4b — Multi-Provider)
        services.AddSingleton<AiSettingsService>();
        services.AddSingleton<AiProviderFactory>();
        services.AddSingleton<TranslationService>();
        services.AddHttpClient(); // AddSingleton<IHttpClientFactory>

        // ViewModels
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<SaveWindowViewModel>();
        services.AddTransient<SettingsWindowViewModel>();
        // OllamaPullViewModel wird ad-hoc mit Modellnamen erzeugt, kein DI-Eintrag.

        return services.BuildServiceProvider();
    }
}
