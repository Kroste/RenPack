using System.Collections.ObjectModel;
using System.Globalization;
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
    private readonly List<SaveVariableViewModel> _allVariables = [];

    /// <summary>Von der View gesetzt (Datei-Dialoge, Meldungen).</summary>
    public ISaveUi? Ui { get; set; }

    public SaveWindowViewModel(IRenpySaveService saveService)
    {
        _saveService = saveService;
    }

    // Designer-Konstruktor
    public SaveWindowViewModel() : this(new RenpySaveService()) { }

    public ObservableCollection<SaveVariableViewModel> Variables { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSave))]
    [NotifyPropertyChangedFor(nameof(SaveSummary))]
    [NotifyPropertyChangedFor(nameof(HasScreenshot))]
    [NotifyPropertyChangedFor(nameof(HasLogError))]
    private SaveInfo? _save;

    [ObservableProperty] private Bitmap? _screenshot;
    [ObservableProperty] private string _statusText = "Kein Save geladen.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _showInternal;

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

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task OpenSaveAsync()
    {
        if (Ui is null) return;
        string? path = await Ui.PickOpenSaveAsync();
        if (path is null) return;
        await LoadSaveAsync(path);
    }

    public async Task LoadSaveAsync(string path)
    {
        if (Ui is null) return;
        IsBusy = true;
        StatusText = "Lese Save …";
        try
        {
            var info = await Task.Run(() => _saveService.Read(path));
            Save = info;
            Screenshot = LoadScreenshot(info.ScreenshotBytes);

            _allVariables.Clear();
            foreach (var v in info.Variables) _allVariables.Add(new SaveVariableViewModel(v));
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

    private void ApplyFilter()
    {
        Variables.Clear();
        IEnumerable<SaveVariableViewModel> src = _allVariables;
        if (!ShowInternal) src = src.Where(v => !v.IsInternal);
        if (!string.IsNullOrWhiteSpace(FilterText))
            src = src.Where(v => v.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
        foreach (var v in src) Variables.Add(v);
    }

    private bool CanInteract() => !IsBusy;
}
