using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;
using RenPack.Localization;
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
        _selectedUiCulture = SupportedUiCultures.FirstOrDefault(c => c.Iso == s.UiCulture)
                             ?? SupportedUiCultures[0];
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

    /// <summary>UI-Sprachen (Anzeige-Reihenfolge = wie in
    /// <see cref="LocalizationService.SupportedCultures"/>).
    /// Element-Typ ist der Tupel <c>(Iso, Display)</c>, damit die ComboBox
    /// den nativen Sprachnamen zeigt (English, Deutsch, Français, Русский)
    /// unabhaengig von der aktuell aktiven UI-Sprache.</summary>
    public IReadOnlyList<UiCultureOption> SupportedUiCultures { get; } =
        LocalizationService.SupportedCultures
            .Select(c => new UiCultureOption(c.Iso, c.Display))
            .ToList();

    /// <summary>Aktuell in der ComboBox ausgewaehlte UI-Sprache. Setter
    /// wechselt die Sprache SOFORT (Live-Wechsel via
    /// <see cref="LocalizationService"/>), damit der Nutzer die
    /// Aenderung direkt sieht — persistiert wird sie erst beim Klick
    /// auf "Speichern".</summary>
    [ObservableProperty]
    private UiCultureOption _selectedUiCulture = new("en", "English");

    partial void OnSelectedUiCultureChanged(UiCultureOption value)
    {
        LocalizationService.Instance.SetCulture(value.Iso);
    }

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
    //
    // Modellauswahl-Muster (nach Magnat-Vorbild): pro Provider gibt es ZWEI Ein-
    // gabemöglichkeiten. Die ComboBox listet die aktuell verfügbaren Modelle
    // (bei Ollama: installiert via /api/tags; bei Cloud-Providern: kuratierte
    // Liste). Die TextBox darunter ist immer editierbar für Fälle, wo das
    // gewünschte Modell nicht in der Liste steht. Beim ComboBox-Select wird der
    // Wert in die Text-Property übernommen (Text ist die "Wahrheit").

    [ObservableProperty] private string _ollamaEndpoint;
    [ObservableProperty] private string _ollamaModel;
    [ObservableProperty] private string? _selectedInstalledOllamaModel;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PullRecommendedModelCommand))]
    private OllamaCuratedModel? _selectedRecommendedModel;

    [ObservableProperty] private string _anthropicEndpoint;
    [ObservableProperty] private string _anthropicModel;
    [ObservableProperty] private string? _selectedAnthropicModel;
    [ObservableProperty] private string _anthropicApiKey;

    [ObservableProperty] private string _openAiEndpoint;
    [ObservableProperty] private string _openAiModel;
    [ObservableProperty] private string? _selectedOpenAiModel;
    [ObservableProperty] private string _openAiApiKey;

    [ObservableProperty] private string _geminiEndpoint;
    [ObservableProperty] private string _geminiModel;
    [ObservableProperty] private string? _selectedGeminiModel;
    [ObservableProperty] private string _geminiApiKey;

    [ObservableProperty] private string _mistralEndpoint;
    [ObservableProperty] private string _mistralModel;
    [ObservableProperty] private string? _selectedMistralModel;
    [ObservableProperty] private string _mistralApiKey;

    [ObservableProperty] private string _openAiCompatibleEndpoint;
    [ObservableProperty] private string _openAiCompatibleModel;
    [ObservableProperty] private string _openAiCompatibleApiKey;

    // ComboBox-Select propagiert in die Text-Property, die die eigentliche
    // Wahrheit ist (wird beim Speichern gelesen). Der Nutzer kann jederzeit
    // manuell überschreiben.
    partial void OnSelectedInstalledOllamaModelChanged(string? value) { if (!string.IsNullOrEmpty(value)) OllamaModel = value; }
    partial void OnSelectedAnthropicModelChanged(string? value)       { if (!string.IsNullOrEmpty(value)) AnthropicModel = value; }
    partial void OnSelectedOpenAiModelChanged(string? value)          { if (!string.IsNullOrEmpty(value)) OpenAiModel = value; }
    partial void OnSelectedGeminiModelChanged(string? value)          { if (!string.IsNullOrEmpty(value)) GeminiModel = value; }
    partial void OnSelectedMistralModelChanged(string? value)         { if (!string.IsNullOrEmpty(value)) MistralModel = value; }

    // --- Ollama Aktionen -----

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task RefreshOllamaModelsAsync()
    {
        IsBusy = true;
        StatusText = L.T("Settings_QueryingOllama");
        try
        {
            var provider = new OllamaProvider(_httpFactory.CreateClient("ai"), OllamaEndpoint, OllamaModel);
            var models = await provider.ListModelsAsync();
            AvailableOllamaModels.Clear();
            foreach (var m in models) AvailableOllamaModels.Add(m);
            StatusText = models.Count == 0
                ? L.T("Settings_OllamaEmpty")
                : L.F("Settings_OllamaCountFormat", models.Count);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Ollama-Modellliste konnte nicht geladen werden");
            StatusText = L.F("Settings_OllamaUnreachableFormat", ex.Message);
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
            StatusText = L.T("Settings_PullNoModelName");
            return;
        }
        RequestOllamaPull?.Invoke(OllamaModel);
    }

    /// <summary>Zieht das aktuell in der Empfehlungs-ComboBox ausgewählte Modell.
    /// Enabled nur, wenn dort etwas gewählt ist (Magnat-Muster: eine ComboBox
    /// mit reichem DataTemplate + ein "Herunterladen"-Button daneben).</summary>
    [RelayCommand(CanExecute = nameof(CanPullRecommended))]
    private void PullRecommendedModel()
    {
        if (SelectedRecommendedModel is null) return;
        RequestOllamaPull?.Invoke(SelectedRecommendedModel.Name);
    }
    private bool CanPullRecommended() => !IsBusy && SelectedRecommendedModel is not null;

    // --- Test & Speichern -----

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task TestConnectionAsync()
    {
        IsBusy = true;
        StatusText = L.T("Settings_TestingConnection");
        try
        {
            // Aktuelle UI-Werte als Settings zusammenbauen, ohne zu speichern.
            var tempSettings = BuildSettings();
            var provider = _factory.Create(tempSettings);
            if (provider is null)
            {
                StatusText = L.T("Settings_TestNoProvider");
                return;
            }
            var probe = await provider.TranslateBatchAsync(["money"], TargetLanguage);
            StatusText = probe.TryGetValue("money", out var t)
                ? L.F("Settings_TestOkFormat", t)
                : L.T("Settings_TestEmpty");
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Testverbindung fehlgeschlagen");
            StatusText = L.F("Settings_TestFailedFormat", ex.Message);
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
        UiCulture: SelectedUiCulture.Iso,
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

/// <summary>Anzeige-Eintrag fuer den UI-Sprach-Selektor in den Einstellungen.
/// <see cref="Display"/> ist der native Sprachname
/// (English/Deutsch/Français/Русский), damit die ComboBox unabhaengig von
/// der aktuell aktiven Sprache alle Optionen lesbar zeigt.</summary>
public sealed record UiCultureOption(string Iso, string Display)
{
    public override string ToString() => Display;
}

/// <summary>Nur für den XAML-Designer-Konstruktor.</summary>
internal sealed class SingleHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
