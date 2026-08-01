using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RenPack.Services;

namespace RenPack.ViewModels;

/// <summary>Diff-Ansicht: vergleicht die Store-Variablen zweier
/// Ren'Py-Save-Dateien. Zeigt Added / Removed / Modified / Unchanged
/// mit Filter "nur Unterschiede" und Name-Filter.</summary>
public sealed partial class SaveDiffViewModel : ObservableObject
{
    private readonly List<SaveDiffRow> _allRows = [];

    public ObservableCollection<SaveDiffRow> Rows { get; } = [];

    [ObservableProperty] private string _leftPath = "";
    [ObservableProperty] private string _rightPath = "";
    [ObservableProperty] private string _summary = "";

    private string _filterText = "";
    public string FilterText
    {
        get => _filterText;
        set { if (SetProperty(ref _filterText, value)) ApplyFilter(); }
    }

    private bool _onlyDifferences = true;
    public bool OnlyDifferences
    {
        get => _onlyDifferences;
        set { if (SetProperty(ref _onlyDifferences, value)) ApplyFilter(); }
    }

    public void Load(SaveInfo left, SaveInfo right)
    {
        LeftPath = left.SavePath;
        RightPath = right.SavePath;

        var leftMap = left.Variables.ToDictionary(v => v.Name, v => v, StringComparer.Ordinal);
        var rightMap = right.Variables.ToDictionary(v => v.Name, v => v, StringComparer.Ordinal);
        var allNames = leftMap.Keys.Union(rightMap.Keys, StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal);

        _allRows.Clear();
        int added = 0, removed = 0, modified = 0, unchanged = 0;
        foreach (var name in allNames)
        {
            leftMap.TryGetValue(name, out var lv);
            rightMap.TryGetValue(name, out var rv);
            var change = (lv, rv) switch
            {
                (null, not null) => DiffChange.Added,
                (not null, null) => DiffChange.Removed,
                (not null, not null) when lv.Value == rv.Value => DiffChange.Unchanged,
                _ => DiffChange.Modified,
            };
            _allRows.Add(new SaveDiffRow(
                Name: name,
                TypeName: (lv ?? rv)!.TypeName,
                LeftValue: lv?.Value ?? "",
                RightValue: rv?.Value ?? "",
                Change: change));

            switch (change)
            {
                case DiffChange.Added: added++; break;
                case DiffChange.Removed: removed++; break;
                case DiffChange.Modified: modified++; break;
                case DiffChange.Unchanged: unchanged++; break;
            }
        }

        Summary = Localization.L.F("Diff_SummaryFormat", added, removed, modified, unchanged);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        Rows.Clear();
        IEnumerable<SaveDiffRow> src = _allRows;
        if (OnlyDifferences) src = src.Where(r => r.Change != DiffChange.Unchanged);
        if (!string.IsNullOrWhiteSpace(FilterText))
            src = src.Where(r => r.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
        foreach (var r in src) Rows.Add(r);
    }

    /// <summary>Vom SaveWindowViewModel gesetzt, wenn das Diff-Fenster
    /// aus einer geladenen Save-Session geoeffnet wurde. Der Callback
    /// bekommt die ausgewaehlte Zeile und uebernimmt den Wert aus B
    /// in die aktive Session — dort greifen Dirty/Undo/Save normal.
    /// Ohne Callback (z.B. Diff aus dem Nichts) ist der Menu-Punkt
    /// deaktiviert.</summary>
    public Action<SaveDiffRow>? OnMigrateFromRight { get; set; }

    [ObservableProperty] private SaveDiffRow? _selectedRow;

    [RelayCommand(CanExecute = nameof(CanMigrateFromRight))]
    private void MigrateFromRight()
    {
        if (SelectedRow is null) return;
        OnMigrateFromRight?.Invoke(SelectedRow);
    }

    private bool CanMigrateFromRight() =>
        OnMigrateFromRight is not null
        && SelectedRow is not null
        && SelectedRow.Change is DiffChange.Modified or DiffChange.Added;

    partial void OnSelectedRowChanged(SaveDiffRow? value)
        => MigrateFromRightCommand.NotifyCanExecuteChanged();
}

public enum DiffChange { Unchanged, Added, Removed, Modified }

public sealed record SaveDiffRow(
    string Name, string TypeName, string LeftValue, string RightValue, DiffChange Change)
{
    /// <summary>Emoji-Marker fuer den Change-Typ (bleibt in allen
    /// Sprachen identisch, keine Uebersetzung noetig).</summary>
    public string Marker => Change switch
    {
        DiffChange.Added => "+ ",
        DiffChange.Removed => "− ",
        DiffChange.Modified => "≠ ",
        _ => "= ",
    };
}
