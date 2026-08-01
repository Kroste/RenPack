using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RenPack.Services;

namespace RenPack.ViewModels;

/// <summary>Diff-Ansicht fuer zwei .rpa-Archive — welche Dateien sind
/// neu (nur in B), entfernt (nur in A), veraendert (Groesse anders)
/// oder unveraendert. Nutzt Datei-Groesse fuer die Modified-Erkennung
/// (der schnelle Weg — Byte-Level-Check waere doppelter Extract).</summary>
public sealed partial class ArchiveDiffViewModel : ObservableObject
{
    private readonly List<ArchiveDiffRow> _allRows = [];

    public ObservableCollection<ArchiveDiffRow> Rows { get; } = [];

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

    public void Load(RpaArchiveInfo left, RpaArchiveInfo right)
    {
        LeftPath = left.ArchivePath;
        RightPath = right.ArchivePath;

        var leftMap = left.Entries.ToDictionary(e => e.Path, e => e, StringComparer.Ordinal);
        var rightMap = right.Entries.ToDictionary(e => e.Path, e => e, StringComparer.Ordinal);
        var allPaths = leftMap.Keys.Union(rightMap.Keys, StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal);

        _allRows.Clear();
        int added = 0, removed = 0, modified = 0, unchanged = 0;
        foreach (var path in allPaths)
        {
            leftMap.TryGetValue(path, out var le);
            rightMap.TryGetValue(path, out var re);
            var change = (le, re) switch
            {
                (null, not null) => DiffChange.Added,
                (not null, null) => DiffChange.Removed,
                (not null, not null) when le.Size == re.Size => DiffChange.Unchanged,
                _ => DiffChange.Modified,
            };
            _allRows.Add(new ArchiveDiffRow(
                Path: path,
                LeftSize: le?.Size ?? 0,
                RightSize: re?.Size ?? 0,
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
        IEnumerable<ArchiveDiffRow> src = _allRows;
        if (OnlyDifferences) src = src.Where(r => r.Change != DiffChange.Unchanged);
        if (!string.IsNullOrWhiteSpace(FilterText))
            src = src.Where(r => r.Path.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
        foreach (var r in src) Rows.Add(r);
    }
}

public sealed record ArchiveDiffRow(string Path, long LeftSize, long RightSize, DiffChange Change)
{
    public string Marker => Change switch
    {
        DiffChange.Added => "+ ",
        DiffChange.Removed => "− ",
        DiffChange.Modified => "≠ ",
        _ => "= ",
    };

    public string LeftDisplay => LeftSize > 0 ? ArchiveEntryViewModel.FormatSize(LeftSize) : "—";
    public string RightDisplay => RightSize > 0 ? ArchiveEntryViewModel.FormatSize(RightSize) : "—";
}
