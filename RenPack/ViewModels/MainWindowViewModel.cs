using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;
using RenPack.Services;

namespace RenPack.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly IRenpyArchiveService _archiveService;
    private readonly List<ArchiveEntryViewModel> _allEntries = [];

    /// <summary>Von der View gesetzt (Datei-/Ordnerdialoge, Meldungen).</summary>
    public IUiInteractions? Ui { get; set; }

    public MainWindowViewModel(IRenpyArchiveService archiveService)
    {
        _archiveService = archiveService;
    }

    // Designer-Konstruktor
    public MainWindowViewModel() : this(new RenpyArchiveService()) { }

    /// <summary>Die aktuell gefilterte, angezeigte Dateiliste.</summary>
    public ObservableCollection<ArchiveEntryViewModel> Entries { get; } = [];

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

    [ObservableProperty] private string _statusText = "Kein Archiv geladen.";
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
        : $"{Archive.Version.ToDisplay()}  ·  {_allEntries.Count} Dateien  ·  {ArchiveEntryViewModel.FormatSize(Archive.TotalSize)}";

    public string SelectedSummary => SelectedCount > 0 ? $"{SelectedCount} ausgewählt" : "";

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
        StatusText = "Lese Archiv …";
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
            StatusText = $"Geladen: {System.IO.Path.GetFileName(path)}";
            Log.Info("Archiv geladen: {path} ({count} Einträge)", path, info.Entries.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Archiv konnte nicht geladen werden: {path}", path);
            Archive = null;
            _allEntries.Clear();
            Entries.Clear();
            StatusText = "Fehler beim Laden.";
            await Ui.ShowMessageAsync("Archiv konnte nicht geladen werden", ex.Message);
        }
        finally
        {
            IsBusy = false;
            ProgressIndeterminate = false;
        }
    }

    // ---- Extrahieren --------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanExtractAll))]
    private Task ExtractAllAsync() => ExtractAsync(_allEntries.Select(e => e.Entry).ToList(), "Alle Dateien");

    [RelayCommand(CanExecute = nameof(CanExtractSelected))]
    private Task ExtractSelectedAsync() =>
        ExtractAsync(_allEntries.Where(e => e.IsSelected).Select(e => e.Entry).ToList(), "Auswahl");

    private async Task ExtractAsync(IReadOnlyList<RpaEntry> entries, string what)
    {
        if (Ui is null || Archive is null || entries.Count == 0) return;
        string? dest = await Ui.PickFolderAsync($"Zielordner für {what.ToLowerInvariant()}");
        if (dest is null) return;

        var archive = Archive;
        IsBusy = true;
        Progress = 0;
        var progress = new Progress<RpaProgress>(p =>
        {
            Progress = p.Fraction;
            StatusText = $"Entpacke {p.Current}/{p.Total}: {p.CurrentFile}";
        });
        try
        {
            int count = await Task.Run(() => _archiveService.Extract(archive, entries, dest, progress));
            StatusText = $"{count} Datei(en) entpackt nach {dest}";
            Log.Info("{count} Datei(en) entpackt nach {dest}", count, dest);
            await Ui.ShowMessageAsync("Fertig", $"{count} Datei(en) entpackt nach:\n{dest}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fehler beim Entpacken");
            StatusText = "Fehler beim Entpacken.";
            await Ui.ShowMessageAsync("Fehler beim Entpacken", ex.Message);
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
        string? sourceDir = await Ui.PickFolderAsync("Ordner zum Verpacken auswählen");
        if (sourceDir is null) return;
        string? target = await Ui.PickSaveArchiveAsync("archive.rpa");
        if (target is null) return;

        IsBusy = true;
        Progress = 0;
        var progress = new Progress<RpaProgress>(p =>
        {
            Progress = p.Fraction;
            StatusText = $"Packe {p.Current}/{p.Total}: {p.CurrentFile}";
        });
        try
        {
            int count = await Task.Run(() =>
                _archiveService.Create(target, sourceDir, RpaVersion.V3_0, RenpyArchiveService.DefaultKey, progress));
            StatusText = $"{count} Datei(en) in {System.IO.Path.GetFileName(target)} gepackt.";
            Log.Info("{count} Datei(en) gepackt: {target}", count, target);
            bool open = await Ui.ConfirmAsync("Archiv erstellt",
                $"{count} Datei(en) in RPA-3.0-Archiv gepackt:\n{target}\n\nDas neue Archiv jetzt öffnen?");
            if (open) await LoadArchiveAsync(target);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fehler beim Erstellen des Archivs");
            StatusText = "Fehler beim Packen.";
            await Ui.ShowMessageAsync("Fehler beim Erstellen", ex.Message);
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
