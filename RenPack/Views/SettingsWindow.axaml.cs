using Microsoft.Extensions.DependencyInjection;
using NLog;
using RenPack.Services;
using RenPack.ViewModels;

namespace RenPack.Views;

public partial class SettingsWindow : ChromeWindow, ISettingsUi
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public SettingsWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is SettingsWindowViewModel vm)
        {
            vm.Ui = this;
            vm.RequestOllamaPull -= OnRequestOllamaPull; // Idempotenz
            vm.RequestOllamaPull += OnRequestOllamaPull;
        }
    }

    private async void OnRequestOllamaPull(string modelName)
    {
        try
        {
            var factory = App.Services.GetRequiredService<AiProviderFactory>();
            var settings = App.Services.GetRequiredService<AiSettingsService>().Current;
            var ollama = factory.CreateOllama(settings);
            var pullVm = new OllamaPullViewModel(ollama, modelName);
            var pullWin = new OllamaPullWindow { DataContext = pullVm };
            await pullWin.ShowDialog(this);
            // Nach dem Pull die Modell-Liste im Settings-Fenster refreshen.
            // WICHTIG: KEIN Task.Run drumherum — der RelayCommand triggert intern
            // CanExecuteChanged, was Avalonia-Controls antasten will; auf einem
            // Background-Thread ergibt das InvalidOperationException ("calling
            // thread cannot access this object"). Wir sind nach ShowDialog wieder
            // auf dem UI-Thread, ExecuteAsync läuft direkt hier.
            if (DataContext is SettingsWindowViewModel vm)
                await vm.RefreshOllamaModelsCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ollama-Pull-Fenster konnte nicht geöffnet werden");
        }
    }

    public void Close(bool saved) => Close((object?)saved);
}
