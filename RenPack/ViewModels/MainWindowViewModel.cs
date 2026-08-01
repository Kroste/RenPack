using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;
using RenPack.Localization;
using RenPack.Services;

namespace RenPack.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly IRenpyArchiveService _archiveService;
    private readonly RecentFilesService? _recent;
    private readonly List<ArchiveEntryViewModel> _allEntries = [];

    /// <summary>Von der View gesetzt (Datei-/Ordnerdialoge, Meldungen).</summary>
    public IUiInteractions? Ui { get; set; }

    public MainWindowViewModel(IRenpyArchiveService archiveService, RecentFilesService recent,
        MediaPlaybackService media)
    {
        _archiveService = archiveService;
        _recent = recent;
        Preview = new PreviewViewModel(archiveService, media);
        RecentArchives = new(_recent.Archives);
        _recent.Changed += (_, _) => RefreshRecent();
    }

    // Designer-Konstruktor
    public MainWindowViewModel() : this(new RenpyArchiveService(), new RecentFilesService(), new MediaPlaybackService()) { }

    /// <summary>MRU-Liste der zuletzt geoeffneten Archive — im Dropdown
    /// neben dem "Oeffnen"-Button gebunden.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> RecentArchives { get; } = [];

    public bool HasRecentArchives => RecentArchives.Count > 0;

    private void RefreshRecent()
    {
        if (_recent is null) return;
        RecentArchives.Clear();
        foreach (var p in _recent.Archives) RecentArchives.Add(p);
        OnPropertyChanged(nameof(HasRecentArchives));
    }

    [RelayCommand]
    private async Task OpenRecentAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        await LoadArchiveAsync(path);
    }

    /// <summary>Die aktuell gefilterte, angezeigte Dateiliste.</summary>
    public ObservableCollection<ArchiveEntryViewModel> Entries { get; } = [];

    /// <summary>Preview-Panel neben der Dateiliste. Wird bei
    /// <see cref="HighlightedEntry"/>-Wechsel neu geladen.</summary>
    public PreviewViewModel Preview { get; }

    [ObservableProperty] private ArchiveEntryViewModel? _highlightedEntry;

    partial void OnHighlightedEntryChanged(ArchiveEntryViewModel? value)
    {
        if (value is null || Archive is null) { Preview.Clear(); return; }
        _ = Preview.LoadAsync(Archive.ArchivePath, value.Entry);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArchive))]
    [NotifyPropertyChangedFor(nameof(ArchiveSummary))]
    [NotifyCanExecuteChangedFor(nameof(ExtractAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExtractSelectedCommand))]
    private RpaArchiveInfo? _archive;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenArchiveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExtractAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExtractSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateArchiveCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _statusText = L.T("Status_ArchiveEmpty");
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _progressIndeterminate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSummary))]
    [NotifyCanExecuteChangedFor(nameof(ExtractSelectedCommand))]
    private int _selectedCount;

    private string _filterText = "";
    public string FilterText
    {
        get => _filterText;
        set { if (SetProperty(ref _filterText, value)) ApplyFilter(); }
    }

    public bool HasArchive => Archive is not null;

    public string ArchiveSummary => Archive is null
        ? ""
        : $"{Archive.Version.ToDisplay()}  ·  {_allEntries.Count} {L.T("Common_Files")}  ·  {ArchiveEntryViewModel.FormatSize(Archive.TotalSize)}";

    public string SelectedSummary => SelectedCount > 0 ? L.F("Status_SelectedFormat", SelectedCount) : "";

    // ---- Öffnen -------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task OpenArchiveAsync()
    {
        if (Ui is null) return;
        string? path = await Ui.PickOpenArchiveAsync();
        if (path is null) return;
        await LoadArchiveAsync(path);
    }

    public async Task LoadArchiveAsync(string path)
    {
        if (Ui is null) return;
        IsBusy = true;
        ProgressIndeterminate = true;
        StatusText = L.T("Status_ArchiveLoading");
        try
        {
            var info = await Task.Run(() => _archiveService.ReadIndex(path));
            Archive = info;
            _allEntries.Clear();
            foreach (var e in info.Entries)
            {
                var vm = new ArchiveEntryViewModel(e);
                vm.PropertyChanged += OnEntryChanged;
                _allEntries.Add(vm);
            }
            SelectedCount = 0;
            ApplyFilter();
            StatusText = L.F("Status_ArchiveLoadedFormat", System.IO.Path.GetFileName(path), info.Entries.Count);
            _recent?.AddArchive(path);
            Log.Info("Archiv geladen: {path} ({count} Einträge)", path, info.Entries.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Archiv konnte nicht geladen werden: {path}", path);
            Archive = null;
            _allEntries.Clear();
            Entries.Clear();
            StatusText = L.T("Status_LoadFailed");
            await Ui.ShowMessageAsync(L.T("Msg_ArchiveLoadFailed_Title"), ex.Message);
        }
        finally
        {
            IsBusy = false;
            ProgressIndeterminate = false;
        }
    }

    // ---- Extrahieren --------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanExtractAll))]
    private Task ExtractAllAsync() => ExtractAsync(_allEntries.Select(e => e.Entry).ToList(), all: true);

    [RelayCommand(CanExecute = nameof(CanExtractSelected))]
    private Task ExtractSelectedAsync() =>
        ExtractAsync(_allEntries.Where(e => e.IsSelected).Select(e => e.Entry).ToList(), all: false);

    private async Task ExtractAsync(IReadOnlyList<RpaEntry> entries, bool all)
    {
        if (Ui is null || Archive is null || entries.Count == 0) return;
        string what = L.T(all ? "Extract_All" : "Extract_Selection");
        string? dest = await Ui.PickFolderAsync(L.F("Msg_PickDestFormat", what));
        if (dest is null) return;

        var archive = Archive;
        IsBusy = true;
        Progress = 0;
        var progress = new Progress<RpaProgress>(p =>
        {
            Progress = p.Fraction;
            StatusText = L.F("Status_ExtractingProgressFormat", p.Current, p.Total, p.CurrentFile);
        });
        try
        {
            int count = await Task.Run(() => _archiveService.Extract(archive, entries, dest, progress));
            StatusText = L.F("Status_ExtractDoneFormat", count, dest);
            Log.Info("{count} Datei(en) entpackt nach {dest}", count, dest);
            await Ui.ShowMessageAsync(L.T("Msg_ExtractDone_Title"), L.F("Msg_ExtractDone_Body_Format", count, dest));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fehler beim Entpacken");
            StatusText = L.T("Status_ExtractFailed");
            await Ui.ShowMessageAsync(L.T("Msg_ExtractFailed_Title"), ex.Message);
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    // ---- Erstellen ----------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task CreateArchiveAsync()
    {
        if (Ui is null) return;
        string? sourceDir = await Ui.PickFolderAsync(L.T("Msg_PickSourceFolder"));
        if (sourceDir is null) return;
        string? target = await Ui.PickSaveArchiveAsync("archive.rpa");
        if (target is null) return;

        // Neues Optionen-Dialog: Format + Key waehlen. Cancel bricht den
        // gesamten Pack-Vorgang ab (der Nutzer hat sich's anders ueberlegt).
        var options = await Ui.AskPackOptionsAsync();
        if (options is null) return;

        IsBusy = true;
        Progress = 0;
        var progress = new Progress<RpaProgress>(p =>
        {
            Progress = p.Fraction;
            StatusText = L.F("Status_PackingProgressFormat", p.Current, p.Total, p.CurrentFile);
        });
        try
        {
            int count = await Task.Run(() =>
                _archiveService.Create(target, sourceDir, options.ResultFormat, options.ResultKey, progress));
            StatusText = L.F("Status_PackDoneFormat", count, System.IO.Path.GetFileName(target));
            Log.Info("{count} Datei(en) als {fmt} gepackt: {target}", count, options.ResultFormat, target);
            bool open = await Ui.ConfirmAsync(L.T("Msg_ArchiveCreated_Title"),
                L.F("Msg_ArchiveCreated_Body_Format", count, target));
            if (open) await LoadArchiveAsync(target);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fehler beim Erstellen des Archivs");
            StatusText = L.T("Status_PackFailed");
            await Ui.ShowMessageAsync(L.T("Msg_CreateFailed_Title"), ex.Message);
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    // ---- Auswahl ------------------------------------------------------------

    [RelayCommand]
    private void SelectAll() { foreach (var e in Entries) e.IsSelected = true; }

    [RelayCommand]
    private void DeselectAll() { foreach (var e in _allEntries) e.IsSelected = false; }

    /// <summary>F5 / Reload: aktuelles Archiv frisch vom Datentraeger einlesen
    /// (falls jemand aussen etwas veraendert hat). No-op wenn nichts offen.</summary>
    [RelayCommand]
    private Task ReloadAsync() => Archive is null ? Task.CompletedTask : LoadArchiveAsync(Archive.ArchivePath);

    /// <summary>Filter leeren (Esc-Hotkey).</summary>
    [RelayCommand]
    private void ClearFilter() => FilterText = "";

    /// <summary>Doppelklick / Kontextmenue: nur die aktuell hervorgehobene
    /// Datei entpacken (mit Datei-Picker fuer den Zielpfad).</summary>
    [RelayCommand]
    private async Task ExtractHighlightedAsync()
    {
        if (Ui is null || Archive is null || HighlightedEntry is null) return;
        string? target = await Ui.PickSaveArchiveOrFileAsync(
            System.IO.Path.GetFileName(HighlightedEntry.Path));
        if (target is null) return;

        var archive = Archive;
        var entry = HighlightedEntry.Entry;
        IsBusy = true;
        try
        {
            await Task.Run(() => _archiveService.ExtractEntry(archive.ArchivePath, entry, target));
            StatusText = L.F("Status_ExtractDoneFormat", 1, target);
            Log.Info("Einzelne Datei extrahiert: {path} → {target}", entry.Path, target);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Einzel-Extraktion fehlgeschlagen");
            StatusText = L.T("Status_ExtractFailed");
            await Ui.ShowMessageAsync(L.T("Msg_ExtractFailed_Title"), ex.Message);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Batch-Extract: mehrere Archive gleichzeitig in einen
    /// Zielordner entpacken. Pro Archiv wird ein Unterordner unter dem
    /// Ziel angelegt (Name ohne .rpa-Extension), damit sich die Inhalte
    /// nicht ueberschreiben.</summary>
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task BatchExtractAsync()
    {
        if (Ui is null) return;
        var archives = await Ui.PickOpenArchivesAsync();
        if (archives.Count == 0) return;
        string? destRoot = await Ui.PickFolderAsync(L.F("Msg_PickDestFormat", L.T("Extract_All")));
        if (destRoot is null) return;

        IsBusy = true;
        Progress = 0;
        int totalArchives = archives.Count;
        int okArchives = 0, failedArchives = 0;
        var errors = new List<string>();

        try
        {
            for (int i = 0; i < archives.Count; i++)
            {
                var arc = archives[i];
                string subFolder = System.IO.Path.Combine(destRoot,
                    System.IO.Path.GetFileNameWithoutExtension(arc));
                StatusText = L.F("Status_BatchExtractProgressFormat", i + 1, totalArchives,
                    System.IO.Path.GetFileName(arc));
                try
                {
                    var info = await Task.Run(() => _archiveService.ReadIndex(arc));
                    await Task.Run(() => _archiveService.ExtractAll(info, subFolder));
                    okArchives++;
                }
                catch (Exception ex)
                {
                    failedArchives++;
                    errors.Add($"{System.IO.Path.GetFileName(arc)}: {ex.Message}");
                    Log.Warn(ex, "Batch-Extract fehlgeschlagen: {arc}", arc);
                }
                Progress = (double)(i + 1) / totalArchives;
            }

            StatusText = L.F("Status_BatchExtractDoneFormat", okArchives, totalArchives, failedArchives);
            string body = failedArchives == 0
                ? L.F("Msg_BatchExtractDone_Body_Format", okArchives, destRoot)
                : L.F("Msg_BatchExtractDoneWithErrors_Body_Format", okArchives, totalArchives, failedArchives)
                    + "\n\n" + string.Join("\n", errors.Take(10));
            await Ui.ShowMessageAsync(L.T("Msg_ExtractDone_Title"), body);
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    /// <summary>Kontextmenue: Pfad des hervorgehobenen Eintrags in die
    /// System-Zwischenablage kopieren.</summary>
    [RelayCommand]
    private async Task CopyHighlightedPathAsync()
    {
        if (Ui is null || HighlightedEntry is null) return;
        await Ui.CopyToClipboardAsync(HighlightedEntry.Path);
        StatusText = L.F("Status_PathCopiedFormat", HighlightedEntry.Path);
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ArchiveEntryViewModel.IsSelected))
            SelectedCount = _allEntries.Count(x => x.IsSelected);
    }

    private void ApplyFilter()
    {
        Entries.Clear();
        IEnumerable<ArchiveEntryViewModel> src = _allEntries;
        if (!string.IsNullOrWhiteSpace(FilterText))
            src = src.Where(e => e.Path.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
        foreach (var e in src) Entries.Add(e);
    }

    // ---- CanExecute ---------------------------------------------------------

    private bool CanInteract() => !IsBusy;
    private bool CanExtractAll() => !IsBusy && HasArchive && _allEntries.Count > 0;
    private bool CanExtractSelected() => !IsBusy && HasArchive && SelectedCount > 0;
}
