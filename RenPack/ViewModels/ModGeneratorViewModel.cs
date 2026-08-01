using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;
using RenPack.Localization;
using RenPack.Services.Modding;

namespace RenPack.ViewModels;

/// <summary>
/// UI-State fuer das Mod-Generator-Fenster. Kapselt den 3-Schritt-Workflow:
/// (1) Quell-Ordner (dekompiliertes Spiel) waehlen,
/// (2) analysieren — zeigt Statistik und Top-Stats,
/// (3) Mod-Typ + Ziel-Ordner waehlen und generieren.
///
/// Aktuell ist nur der Walkthrough-Mod implementiert; die
/// <see cref="AvailableModTypes"/>-Liste ist so aufgebaut dass spaeter
/// Cheat-Mod und Rename-Patch dazukommen koennen ohne UI-Umbau.
/// </summary>
public sealed partial class ModGeneratorViewModel : ObservableObject
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly RenpyModAnalyzer _analyzer = new();
    private readonly KrosteWalkthroughGenerator _walkthrough = new();

    public IModGeneratorUi? Ui { get; set; }

    // ---- Schritt 1: Quelle ------------------------------------------------

    /// <summary>Ordner mit dekompilierten <c>.rpy</c>-Dateien (typischerweise
    /// das <c>game/</c>-Verzeichnis des Spiels nach extract+decompile).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyPropertyChangedFor(nameof(DestinationDirectory))]
    private string _sourceDirectory = "";

    // ---- Schritt 2: Analyse -----------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnalysis))]
    [NotifyPropertyChangedFor(nameof(AnalyzedFilesCount))]
    [NotifyPropertyChangedFor(nameof(ChoiceCount))]
    [NotifyPropertyChangedFor(nameof(StoreVarCount))]
    [NotifyPropertyChangedFor(nameof(CharacterCount))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private ModAnalysis? _analysis;

    public bool HasAnalysis => Analysis is not null;
    public int AnalyzedFilesCount => Analysis?.AnalyzedFiles.Count ?? 0;
    public int ChoiceCount => Analysis?.Choices.Count ?? 0;
    public int StoreVarCount => Analysis?.StoreVariables.Count ?? 0;
    public int CharacterCount => Analysis?.Characters.Count ?? 0;

    /// <summary>Top-Stat-Kandidaten nach Aenderungshaeufigkeit — hilft dem
    /// User zu sehen, welche Variablen das Spiel als „Stats" behandelt
    /// (Vorbereitung fuer den spaeteren Cheat-Mod-Generator in E3).</summary>
    public ObservableCollection<StatCandidate> TopStats { get; } = [];

    // ---- Schritt 3: Ziel + Typ --------------------------------------------

    public ObservableCollection<ModType> AvailableModTypes { get; } =
    [
        new ModType(ModTypeId.Walkthrough, "Walkthrough"),
        // spaeter: new ModType(ModTypeId.Cheat, "Cheat menu"),
        //          new ModType(ModTypeId.Rename, "Character rename (AI)"),
    ];

    [ObservableProperty] private ModType _selectedModType;

    /// <summary>Ziel-Ordner fuer die generierten Mod-Dateien. Default:
    /// <c>&lt;source&gt;/../KrosteMod-&lt;typ&gt;/</c> — direkt neben dem
    /// Spiel-Ordner, damit man's leicht zum Spiel kopieren kann.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private string _destinationDirectory = "";

    partial void OnSourceDirectoryChanged(string value) => UpdateDefaultDest();
    partial void OnSelectedModTypeChanged(ModType value) => UpdateDefaultDest();

    private void UpdateDefaultDest()
    {
        if (string.IsNullOrWhiteSpace(SourceDirectory)) return;
        var parent = System.IO.Path.GetDirectoryName(SourceDirectory.TrimEnd('/', '\\'))
                     ?? SourceDirectory;
        DestinationDirectory = System.IO.Path.Combine(parent,
            $"KrosteMod-{SelectedModType.Id}");
    }

    // ---- Status / Busy ----------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyCanExecuteChangedFor(nameof(PickSourceCommand))]
    [NotifyCanExecuteChangedFor(nameof(PickDestinationCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _statusText = "";

    public ModGeneratorViewModel()
    {
        _selectedModType = AvailableModTypes[0];
    }

    // ---- Commands ---------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task PickSourceAsync()
    {
        if (Ui is null) return;
        var picked = await Ui.PickFolderAsync(L.T("Mod_PickSource_Title"));
        if (picked is not null) SourceDirectory = picked;
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task PickDestinationAsync()
    {
        if (Ui is null) return;
        var picked = await Ui.PickFolderAsync(L.T("Mod_PickDest_Title"));
        if (picked is not null) DestinationDirectory = picked;
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        if (!Directory.Exists(SourceDirectory))
        {
            StatusText = L.T("Mod_SourceNotFound");
            return;
        }
        IsBusy = true;
        StatusText = L.T("Mod_Analyzing");
        try
        {
            var analysis = await Task.Run(() => _analyzer.Analyze(SourceDirectory));
            Analysis = analysis;
            RefreshTopStats(analysis);
            StatusText = L.F("Mod_AnalyzeDoneFormat",
                analysis.AnalyzedFiles.Count, analysis.Choices.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Mod-Analyse fehlgeschlagen");
            StatusText = L.F("Mod_AnalyzeFailedFormat", ex.Message);
        }
        finally { IsBusy = false; }
    }

    private void RefreshTopStats(ModAnalysis analysis)
    {
        TopStats.Clear();
        var stats = analysis.Choices.SelectMany(c => c.Deltas)
            .Where(d => d.Op is "+=" or "-=")
            .GroupBy(d => d.Variable)
            .Select(g => new StatCandidate(g.Key, g.Count()))
            .OrderByDescending(s => s.ChangeCount)
            .Take(10);
        foreach (var s in stats) TopStats.Add(s);
    }

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        if (Ui is null || Analysis is null) return;
        if (string.IsNullOrWhiteSpace(DestinationDirectory))
        {
            StatusText = L.T("Mod_DestRequired");
            return;
        }
        if (Directory.Exists(DestinationDirectory) &&
            Directory.EnumerateFileSystemEntries(DestinationDirectory).Any())
        {
            bool overwrite = await Ui.ConfirmAsync(
                L.T("Mod_DestExists_Title"),
                L.F("Mod_DestExists_Body_Format", DestinationDirectory));
            if (!overwrite) return;
        }

        IsBusy = true;
        StatusText = L.T("Mod_Generating");
        try
        {
            int written = await Task.Run(() => SelectedModType.Id switch
            {
                ModTypeId.Walkthrough => _walkthrough.Generate(
                    SourceDirectory, DestinationDirectory, Analysis),
                _ => throw new NotSupportedException($"Unbekannter Mod-Typ: {SelectedModType.Id}"),
            });
            StatusText = L.F("Mod_GenerateDoneFormat", written, DestinationDirectory);
            await Ui.ShowMessageAsync(
                L.T("Mod_GenerateDone_Title"),
                L.F("Mod_GenerateDone_Body_Format", written, DestinationDirectory));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Mod-Generierung fehlgeschlagen");
            StatusText = L.F("Mod_GenerateFailedFormat", ex.Message);
            await Ui.ShowMessageAsync(L.T("Mod_GenerateFailed_Title"), ex.Message);
        }
        finally { IsBusy = false; }
    }

    private bool CanInteract() => !IsBusy;
    private bool CanAnalyze() => !IsBusy && !string.IsNullOrWhiteSpace(SourceDirectory);
    private bool CanGenerate() => !IsBusy && HasAnalysis
        && !string.IsNullOrWhiteSpace(DestinationDirectory);
}

public interface IModGeneratorUi
{
    Task<string?> PickFolderAsync(string title);
    Task ShowMessageAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message);
}

public enum ModTypeId { Walkthrough, Cheat, Rename }

public sealed record ModType(ModTypeId Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record StatCandidate(string Variable, int ChangeCount);
