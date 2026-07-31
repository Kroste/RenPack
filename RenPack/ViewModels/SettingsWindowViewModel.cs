using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;
using RenPack.Services;

namespace RenPack.ViewModels;

public sealed partial class SettingsWindowViewModel : ObservableObject
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly AiSettingsService _settingsService;
    private readonly IHttpClientFactory _httpFactory;

    public ISettingsUi? Ui { get; set; }

    public SettingsWindowViewModel(AiSettingsService settingsService, IHttpClientFactory httpFactory)
    {
        _settingsService = settingsService;
        _httpFactory = httpFactory;
        var s = settingsService.Current;
        _selectedProvider = s.Provider;
        _ollamaEndpoint = s.OllamaEndpoint;
        _ollamaModel = s.OllamaModel;
        _targetLanguage = s.TargetLanguage;
    }

    // Designer-Konstruktor
    public SettingsWindowViewModel() : this(new AiSettingsService(), new SingleHttpClientFactory()) { }

    public ObservableCollection<AiProviderType> Providers { get; } =
        [AiProviderType.None, AiProviderType.Ollama];

    public ObservableCollection<string> Languages { get; } =
        ["Deutsch", "Englisch", "Französisch", "Spanisch", "Italienisch",
         "Russisch", "Portugiesisch", "Niederländisch", "Polnisch", "Japanisch", "Chinesisch"];

    public ObservableCollection<string> AvailableModels { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOllama))]
    private AiProviderType _selectedProvider;

    [ObservableProperty] private string _ollamaEndpoint;
    [ObservableProperty] private string _ollamaModel;
    [ObservableProperty] private string _targetLanguage;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;

    public bool IsOllama => SelectedProvider == AiProviderType.Ollama;

    partial void OnSelectedProviderChanged(AiProviderType value) => StatusText = "";

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task RefreshModelsAsync()
    {
        IsBusy = true;
        StatusText = "Frage Ollama nach verfügbaren Modellen …";
        try
        {
            var provider = new OllamaProvider(_httpFactory.CreateClient(), OllamaEndpoint, OllamaModel);
            var models = await provider.ListModelsAsync();
            AvailableModels.Clear();
            foreach (var m in models) AvailableModels.Add(m);
            if (models.Count == 0)
                StatusText = "Ollama ist erreichbar, aber es sind noch keine Modelle installiert. " +
                             "Bitte per Terminal ziehen: ollama pull gemma3:1b";
            else
            {
                StatusText = $"{models.Count} Modell(e) gefunden.";
                if (!models.Contains(OllamaModel) && models.Count > 0)
                    OllamaModel = models[0];
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Modellliste konnte nicht geladen werden");
            StatusText = $"Ollama nicht erreichbar: {ex.Message}";
            AvailableModels.Clear();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task TestConnectionAsync()
    {
        IsBusy = true;
        StatusText = "Teste Verbindung …";
        try
        {
            var provider = new OllamaProvider(_httpFactory.CreateClient(), OllamaEndpoint, OllamaModel);
            bool ok = await provider.IsAvailableAsync();
            if (!ok) { StatusText = "Ollama antwortet nicht auf " + OllamaEndpoint; return; }

            var probe = await provider.TranslateBatchAsync(["money"], TargetLanguage);
            StatusText = probe.TryGetValue("money", out var t)
                ? $"Verbindung OK — Test: money → \"{t}\""
                : "Ollama antwortet, aber die Modell-Antwort war leer. Anderes Modell probieren?";
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Testverbindung fehlgeschlagen");
            StatusText = $"Test fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Save()
    {
        _settingsService.Update(new AiSettings(
            Provider: SelectedProvider,
            OllamaEndpoint: OllamaEndpoint.Trim(),
            OllamaModel: OllamaModel.Trim(),
            TargetLanguage: TargetLanguage));
        Ui?.Close(saved: true);
    }

    [RelayCommand]
    private void Cancel() => Ui?.Close(saved: false);

    private bool CanInteract() => !IsBusy;
}

/// <summary>UI-Brücke — bewusst minimal (nur schließen).</summary>
public interface ISettingsUi
{
    void Close(bool saved);
}

/// <summary>Nur für den XAML-Designer-Konstruktor.</summary>
internal sealed class SingleHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
