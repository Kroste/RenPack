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
    private readonly AiProviderFactory _factory;
    private readonly IHttpClientFactory _httpFactory;

    public ISettingsUi? Ui { get; set; }

    public SettingsWindowViewModel(AiSettingsService settingsService,
        AiProviderFactory factory, IHttpClientFactory httpFactory)
    {
        _settingsService = settingsService;
        _factory = factory;
        _httpFactory = httpFactory;

        var s = settingsService.Current;
        _selectedProvider = s.Provider;
        _targetLanguage = s.TargetLanguage;
        _ollamaEndpoint = s.Ollama.Endpoint;
        _ollamaModel = s.Ollama.Model;
        _anthropicEndpoint = s.Anthropic.Endpoint;
        _anthropicModel = s.Anthropic.Model;
        _anthropicApiKey = s.Anthropic.ApiKey ?? "";
        _openAiEndpoint = s.OpenAi.Endpoint;
        _openAiModel = s.OpenAi.Model;
        _openAiApiKey = s.OpenAi.ApiKey ?? "";
        _geminiEndpoint = s.Gemini.Endpoint;
        _geminiModel = s.Gemini.Model;
        _geminiApiKey = s.Gemini.ApiKey ?? "";
        _mistralEndpoint = s.Mistral.Endpoint;
        _mistralModel = s.Mistral.Model;
        _mistralApiKey = s.Mistral.ApiKey ?? "";
        _openAiCompatibleEndpoint = s.OpenAiCompatible.Endpoint;
        _openAiCompatibleModel = s.OpenAiCompatible.Model;
        _openAiCompatibleApiKey = s.OpenAiCompatible.ApiKey ?? "";
    }

    // Designer-ctor
    public SettingsWindowViewModel() : this(new AiSettingsService(),
        new AiProviderFactory(new SingleHttpClientFactory()), new SingleHttpClientFactory()) { }

    public ObservableCollection<AiProviderType> Providers { get; } =
    [
        AiProviderType.None,
        AiProviderType.Ollama,
        AiProviderType.Anthropic,
        AiProviderType.OpenAi,
        AiProviderType.Gemini,
        AiProviderType.Mistral,
        AiProviderType.OpenAiCompatible,
    ];

    public ObservableCollection<string> Languages { get; } =
        ["Deutsch", "Englisch", "Französisch", "Spanisch", "Italienisch",
         "Russisch", "Portugiesisch", "Niederländisch", "Polnisch", "Japanisch", "Chinesisch"];

    public ObservableCollection<string> AvailableOllamaModels { get; } = [];
    public ObservableCollection<OllamaCuratedModel> CuratedOllamaModels { get; } =
        new(OllamaCuratedModels.All);
    public ObservableCollection<string> AnthropicModels { get; } =
        ["claude-opus-4-7", "claude-sonnet-4-6", "claude-haiku-4-5", "claude-3-7-sonnet-latest"];
    public ObservableCollection<string> OpenAiModels { get; } =
        ["gpt-4o", "gpt-4o-mini", "gpt-4-turbo", "o1-mini"];
    public ObservableCollection<string> GeminiModels { get; } =
        ["gemini-2.0-flash", "gemini-2.0-flash-lite", "gemini-2.5-pro", "gemini-2.5-flash"];
    public ObservableCollection<string> MistralModels { get; } =
        ["mistral-small-latest", "mistral-medium-latest", "mistral-large-latest", "codestral-latest"];

    // --- Auswahl -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOllama))]
    [NotifyPropertyChangedFor(nameof(IsAnthropic))]
    [NotifyPropertyChangedFor(nameof(IsOpenAi))]
    [NotifyPropertyChangedFor(nameof(IsGemini))]
    [NotifyPropertyChangedFor(nameof(IsMistral))]
    [NotifyPropertyChangedFor(nameof(IsOpenAiCompatible))]
    private AiProviderType _selectedProvider;

    [ObservableProperty] private string _targetLanguage;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;

    public bool IsOllama => SelectedProvider == AiProviderType.Ollama;
    public bool IsAnthropic => SelectedProvider == AiProviderType.Anthropic;
    public bool IsOpenAi => SelectedProvider == AiProviderType.OpenAi;
    public bool IsGemini => SelectedProvider == AiProviderType.Gemini;
    public bool IsMistral => SelectedProvider == AiProviderType.Mistral;
    public bool IsOpenAiCompatible => SelectedProvider == AiProviderType.OpenAiCompatible;

    // --- Per-Provider Felder -----

    [ObservableProperty] private string _ollamaEndpoint;
    [ObservableProperty] private string _ollamaModel;

    [ObservableProperty] private string _anthropicEndpoint;
    [ObservableProperty] private string _anthropicModel;
    [ObservableProperty] private string _anthropicApiKey;

    [ObservableProperty] private string _openAiEndpoint;
    [ObservableProperty] private string _openAiModel;
    [ObservableProperty] private string _openAiApiKey;

    [ObservableProperty] private string _geminiEndpoint;
    [ObservableProperty] private string _geminiModel;
    [ObservableProperty] private string _geminiApiKey;

    [ObservableProperty] private string _mistralEndpoint;
    [ObservableProperty] private string _mistralModel;
    [ObservableProperty] private string _mistralApiKey;

    [ObservableProperty] private string _openAiCompatibleEndpoint;
    [ObservableProperty] private string _openAiCompatibleModel;
    [ObservableProperty] private string _openAiCompatibleApiKey;

    // --- Ollama Aktionen -----

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task RefreshOllamaModelsAsync()
    {
        IsBusy = true;
        StatusText = "Frage Ollama nach installierten Modellen …";
        try
        {
            var provider = new OllamaProvider(_httpFactory.CreateClient("ai"), OllamaEndpoint, OllamaModel);
            var models = await provider.ListModelsAsync();
            AvailableOllamaModels.Clear();
            foreach (var m in models) AvailableOllamaModels.Add(m);
            StatusText = models.Count == 0
                ? "Ollama erreichbar, aber noch keine Modelle installiert. Nutze den Pull-Button."
                : $"{models.Count} Modell(e) installiert.";
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Ollama-Modellliste konnte nicht geladen werden");
            StatusText = $"Ollama nicht erreichbar: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>Bittet die View, ein <c>OllamaPullWindow</c> für das aktuell
    /// eingetragene Modell zu öffnen. Die View kümmert sich um Fenster-Handling;
    /// nach dem Erfolg wird die Modell-Liste erneut gezogen.</summary>
    public event Action<string>? RequestOllamaPull;

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void PullOllamaModel()
    {
        if (string.IsNullOrWhiteSpace(OllamaModel))
        {
            StatusText = "Bitte zuerst einen Modell-Namen eintragen (z. B. gemma3:1b).";
            return;
        }
        RequestOllamaPull?.Invoke(OllamaModel);
    }

    // --- Test & Speichern -----

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task TestConnectionAsync()
    {
        IsBusy = true;
        StatusText = "Teste Verbindung …";
        try
        {
            // Aktuelle UI-Werte als Settings zusammenbauen, ohne zu speichern.
            var tempSettings = BuildSettings();
            var provider = _factory.Create(tempSettings);
            if (provider is null)
            {
                StatusText = "Kein Provider aktiv (fehlt API-Key?).";
                return;
            }
            var probe = await provider.TranslateBatchAsync(["money"], TargetLanguage);
            StatusText = probe.TryGetValue("money", out var t)
                ? $"OK — Testübersetzung: money → \"{t}\""
                : "Provider antwortet, aber die Antwort war leer. Anderes Modell probieren?";
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Testverbindung fehlgeschlagen");
            StatusText = $"Test fehlgeschlagen: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Save()
    {
        _settingsService.Update(BuildSettings());
        Ui?.Close(saved: true);
    }

    [RelayCommand]
    private void Cancel() => Ui?.Close(saved: false);

    private AiSettings BuildSettings() => new(
        Provider: SelectedProvider,
        TargetLanguage: TargetLanguage,
        Ollama: new AiProviderConfig(OllamaEndpoint.Trim(), OllamaModel.Trim()),
        Anthropic: new AiProviderConfig(AnthropicEndpoint.Trim(), AnthropicModel.Trim(), NullIfEmpty(AnthropicApiKey)),
        OpenAi: new AiProviderConfig(OpenAiEndpoint.Trim(), OpenAiModel.Trim(), NullIfEmpty(OpenAiApiKey)),
        Gemini: new AiProviderConfig(GeminiEndpoint.Trim(), GeminiModel.Trim(), NullIfEmpty(GeminiApiKey)),
        Mistral: new AiProviderConfig(MistralEndpoint.Trim(), MistralModel.Trim(), NullIfEmpty(MistralApiKey)),
        OpenAiCompatible: new AiProviderConfig(OpenAiCompatibleEndpoint.Trim(),
            OpenAiCompatibleModel.Trim(), NullIfEmpty(OpenAiCompatibleApiKey)));

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private bool CanInteract() => !IsBusy;
}

public interface ISettingsUi
{
    void Close(bool saved);
}

/// <summary>Nur für den XAML-Designer-Konstruktor.</summary>
internal sealed class SingleHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
