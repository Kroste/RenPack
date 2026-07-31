using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;
using RenPack.Services;

namespace RenPack.ViewModels;

public sealed partial class SaveWindowViewModel : ObservableObject
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly IRenpySaveService _saveService;
    private readonly AiSettingsService? _aiSettings;
    private readonly AiProviderFactory? _providerFactory;
    private readonly TranslationService? _translation;
    private readonly List<SaveVariableViewModel> _allVariables = [];

    /// <summary>Von der View gesetzt (Datei-Dialoge, Meldungen).</summary>
    public ISaveUi? Ui { get; set; }

    public SaveWindowViewModel(IRenpySaveService saveService, AiSettingsService aiSettings,
        AiProviderFactory providerFactory, TranslationService translation)
    {
        _saveService = saveService;
        _aiSettings = aiSettings;
        _providerFactory = providerFactory;
        _translation = translation;
        // Wenn der Nutzer die KI-Einstellungen nach dem Öffnen des Save-Fensters
        // konfiguriert, muss der Übersetzen-Button neu bewertet werden. Sonst
        // bleibt er grau, obwohl KI jetzt läuft.
        _aiSettings.SettingsChanged += OnAiSettingsChanged;
    }

    private void OnAiSettingsChanged(object? sender, EventArgs e)
        => TranslateCommand.NotifyCanExecuteChanged();

    // Designer-Konstruktor
    public SaveWindowViewModel() : this(new RenpySaveService(), new AiSettingsService(),
        new AiProviderFactory(new SingleHttpClientFactory()), new TranslationService()) { }

    public ObservableCollection<SaveVariableViewModel> Variables { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSave))]
    [NotifyPropertyChangedFor(nameof(SaveSummary))]
    [NotifyPropertyChangedFor(nameof(HasScreenshot))]
    [NotifyPropertyChangedFor(nameof(HasLogError))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAsCommand))]
    private SaveInfo? _save;

    [ObservableProperty] private Bitmap? _screenshot;
    [ObservableProperty] private string _statusText = "Kein Save geladen.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _showInternal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirtySummary))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
    private int _dirtyCount;

    private string _filterText = "";
    public string FilterText
    {
        get => _filterText;
        set { if (SetProperty(ref _filterText, value)) ApplyFilter(); }
    }

    partial void OnShowInternalChanged(bool value) => ApplyFilter();

    public bool HasSave => Save is not null;
    public bool HasScreenshot => Screenshot is not null;
    public bool HasLogError => Save?.LogError is not null;
    public string LogErrorText => Save?.LogError ?? "";
    public string DirtySummary => DirtyCount > 0 ? $"{DirtyCount} geändert" : "";

    public string SaveSummary
    {
        get
        {
            if (Save is null) return "";
            var m = Save.Metadata;
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(m.SaveName)) parts.Add(m.SaveName);
            if (m.SaveTime is { } t) parts.Add(t.ToString("g", CultureInfo.CurrentCulture));
            if (!string.IsNullOrEmpty(m.RenpyVersion)) parts.Add($"Ren'Py {m.RenpyVersion}");
            if (Save.LogError is null) parts.Add($"{_allVariables.Count} Variablen");
            return string.Join("  ·  ", parts);
        }
    }

    // ---- Öffnen / Laden ----------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task OpenSaveAsync()
    {
        if (Ui is null) return;
        if (!await ConfirmDiscardDirtyAsync()) return;
        string? path = await Ui.PickOpenSaveAsync();
        if (path is null) return;
        await LoadSaveAsync(path);
    }

    public async Task LoadSaveAsync(string path)
    {
        if (Ui is null) return;
        if (!await ConfirmDiscardDirtyAsync()) return;
        IsBusy = true;
        StatusText = "Lese Save …";
        try
        {
            var info = await Task.Run(() => _saveService.Read(path));
            Save = info;
            Screenshot = LoadScreenshot(info.ScreenshotBytes);

            _allVariables.Clear();
            foreach (var v in info.Variables)
            {
                var vm = new SaveVariableViewModel(v);
                vm.PropertyChanged += OnVariableChanged;
                // Cache-Hits sofort füllen — dann steht die Übersetzung auch beim
                // Neu-Öffnen einer weiteren Save-Datei desselben Spiels direkt da.
                if (_translation is not null && _translation.TryGetCached(v.Name, out var cached))
                    vm.Description = cached;
                _allVariables.Add(vm);
            }
            DirtyCount = 0;
            // Variablen-Count ist Teil der Enable-Bedingung; neu bewerten.
            TranslateCommand.NotifyCanExecuteChanged();
            ApplyFilter();

            StatusText = info.LogError is null
                ? $"Geladen: {System.IO.Path.GetFileName(path)}"
                : $"Metadaten geladen, Log unlesbar: {System.IO.Path.GetFileName(path)}";
            OnPropertyChanged(nameof(SaveSummary));
            Log.Info("Save geladen: {path} ({count} Variablen, LogError={err})",
                path, info.Variables.Count, info.LogError);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Save konnte nicht geladen werden: {path}", path);
            Save = null;
            Screenshot = null;
            _allVariables.Clear();
            Variables.Clear();
            StatusText = "Fehler beim Laden.";
            await Ui.ShowMessageAsync("Save konnte nicht geladen werden", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> ConfirmDiscardDirtyAsync()
    {
        if (Ui is null || DirtyCount == 0) return true;
        return await Ui.ConfirmAsync("Ungespeicherte Änderungen",
            $"Du hast {DirtyCount} ungespeicherte Änderung(en). Trotzdem verwerfen?");
    }

    // ---- Speichern ---------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => SaveToAsync(Save!.SavePath, overwriteOriginal: true);

    [RelayCommand(CanExecute = nameof(CanSaveAs))]
    private async Task SaveAsAsync()
    {
        if (Ui is null || Save is null) return;
        string suggested = System.IO.Path.GetFileNameWithoutExtension(Save.SavePath) + "-edited.save";
        string? target = await Ui.PickSaveTargetAsync(suggested);
        if (target is null) return;
        await SaveToAsync(target, overwriteOriginal: false);
    }

    [RelayCommand(CanExecute = nameof(CanRevert))]
    private void Revert()
    {
        foreach (var v in _allVariables)
            if (v.IsDirty) v.EditableValue = v.OriginalValue;
        DirtyCount = 0;
        StatusText = "Änderungen verworfen.";
    }

    private async Task SaveToAsync(string target, bool overwriteOriginal)
    {
        if (Ui is null || Save is null) return;
        var dirtyVars = _allVariables.Where(v => v.IsDirty).ToList();
        if (dirtyVars.Count == 0) return;

        IsBusy = true;
        StatusText = "Sammle Änderungen …";
        var edits = new List<SaveEdit>(dirtyVars.Count);
        try
        {
            foreach (var vm in dirtyVars)
                edits.Add(new SaveEdit(vm.Name, vm.ParseEditedValue()));
        }
        catch (Exception ex)
        {
            IsBusy = false;
            StatusText = "Ungültiger Wert.";
            await Ui.ShowMessageAsync("Ungültige Eingabe",
                $"Konnte einen Wert nicht in den passenden Typ konvertieren:\n{ex.Message}");
            return;
        }

        try
        {
            string source = Save.SavePath;
            await Task.Run(() => _saveService.Write(source, target, edits));
            StatusText = overwriteOriginal
                ? $"Gespeichert: {System.IO.Path.GetFileName(target)}"
                : $"Kopie gespeichert: {System.IO.Path.GetFileName(target)}";
            Log.Info("Save geschrieben: {target} ({count} Änderungen)", target, edits.Count);
            // Original neu laden, um DirtyCount zu resetten und aktuelle Baseline zu haben.
            await LoadSaveAsync(overwriteOriginal ? target : source);
            if (!overwriteOriginal)
                await Ui.ShowMessageAsync("Fertig", $"Kopie gespeichert:\n{target}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Save konnte nicht geschrieben werden: {target}", target);
            StatusText = "Fehler beim Speichern.";
            await Ui.ShowMessageAsync("Speichern fehlgeschlagen", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- KI-Übersetzung -----------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanExecuteTranslate))]
    private async Task TranslateAsync()
    {
        if (Ui is null || _translation is null || _aiSettings is null || _providerFactory is null) return;
        var settings = _aiSettings.Current;
        var provider = _providerFactory.Create(settings);
        if (provider is null)
        {
            await Ui.ShowMessageAsync("KI nicht konfiguriert",
                "Wähle in den Einstellungen einen KI-Anbieter und trage die nötigen " +
                "Zugangsdaten ein (bei Cloud-Providern den API-Key).");
            return;
        }

        // Nur die aktuell sichtbaren Variablen übersetzen (respektiert Filter + interne Sichtbarkeit).
        var toTranslate = Variables.Select(v => v.Name).ToList();
        if (toTranslate.Count == 0) return;

        IsBusy = true;
        StatusText = $"Übersetze {toTranslate.Count} Variablen via {provider.Name} …";
        try
        {
            _translation.ResetCacheIfNeeded(provider.Name, settings.TargetLanguage);
            var progress = new Progress<(int done, int total)>(p =>
                StatusText = $"Übersetzt {p.done}/{p.total} …");
            var result = await _translation.TranslateAsync(provider, toTranslate,
                settings.TargetLanguage, progress);

            foreach (var vm in _allVariables)
                if (result.TryGetValue(vm.Name, out var t)) vm.Description = t;

            StatusText = $"Übersetzung fertig: {result.Count} Beschreibungen.";
            Log.Info("Übersetzung fertig: {count}/{req}", result.Count, toTranslate.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Übersetzung fehlgeschlagen");
            StatusText = "Übersetzung fehlgeschlagen.";
            await Ui.ShowMessageAsync("Übersetzung fehlgeschlagen", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Direkt aus den aktuellen Settings ableiten — nichts cachen. Beim
    /// Provider-Wechsel triggert das Event <see cref="AiSettingsService.SettingsChanged"/>
    /// ein <c>NotifyCanExecuteChanged</c>, dann wird die Methode neu ausgewertet.</summary>
    private bool CanExecuteTranslate() =>
        !IsBusy
        && _aiSettings is not null
        && _aiSettings.Current.Provider != AiProviderType.None
        && _allVariables.Count > 0;

    // ---- Filter & Dirty-Tracking -------------------------------------------

    private void OnVariableChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SaveVariableViewModel.EditableValue) ||
            e.PropertyName == nameof(SaveVariableViewModel.IsDirty))
        {
            DirtyCount = _allVariables.Count(v => v.IsDirty);
        }
    }

    private void ApplyFilter()
    {
        Variables.Clear();
        IEnumerable<SaveVariableViewModel> src = _allVariables;
        if (!ShowInternal) src = src.Where(v => !v.IsInternal);
        if (!string.IsNullOrWhiteSpace(FilterText))
            src = src.Where(v => v.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
        foreach (var v in src) Variables.Add(v);
    }

    private static Bitmap? LoadScreenshot(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Screenshot im Save konnte nicht dekodiert werden");
            return null;
        }
    }

    private bool CanInteract() => !IsBusy;
    private bool CanSave() => !IsBusy && HasSave && DirtyCount > 0;
    private bool CanSaveAs() => !IsBusy && HasSave;
    private bool CanRevert() => !IsBusy && DirtyCount > 0;
}
