using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using NLog;
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
        services.AddSingleton<UpdateService>();

        // ViewModels
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<SaveWindowViewModel>();

        return services.BuildServiceProvider();
    }
}
