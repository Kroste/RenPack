using System.Collections.ObjectModel;
using RenPack.Localization;
using RenPack.Services.Modding;

namespace RenPack.Views;

/// <summary>Preview-Dialog fuer die vom KrosteAiRewriter vorgeschlagenen
/// Body-Text-Edits. Jede Zeile hat eine Accepted-Checkbox — Default true.
/// User kann individuell abwaehlen oder mit Accept-All/Reject-All bulk-
/// aendern. Bei OK werden nur die akzeptierten Edits zurueckgegeben.</summary>
public partial class RewritePreviewWindow : ChromeWindow
{
    private readonly ObservableCollection<RewriteRow> _rows = new();

    /// <summary>Nach OK: die vom User akzeptierten Edits. <c>null</c> bei
    /// Cancel/Close.</summary>
    public IReadOnlyList<BodyTextEdit>? Result { get; private set; }

    public RewritePreviewWindow()
    {
        InitializeComponent();
        EditsGrid.ItemsSource = _rows;
        AcceptAllButton.Click += (_, _) => SetAllAccepted(true);
        RejectAllButton.Click += (_, _) => SetAllAccepted(false);
        OkButton.Click += (_, _) => ApplyAndClose();
        CancelButton.Click += (_, _) => { Result = null; Close(); };
    }

    public void Load(IReadOnlyList<BodyTextEdit> proposals)
    {
        _rows.Clear();
        foreach (var edit in proposals.OrderBy(e => e.SourceFile, StringComparer.Ordinal)
                                     .ThenBy(e => e.SourceLine))
        {
            _rows.Add(new RewriteRow
            {
                Location = $"{edit.SourceFile}:{edit.SourceLine}",
                OriginalText = edit.OriginalText,
                NewText = edit.NewText,
                Accepted = edit.Accepted,
                Source = edit,
            });
        }
        UpdateCount();
    }

    private void SetAllAccepted(bool accepted)
    {
        // ObservableCollection-Items direkt setzen loest kein UI-Refresh im
        // DataGrid — wir muessen die Row austauschen. Einfacher: rebuild.
        var snapshot = _rows.ToList();
        _rows.Clear();
        foreach (var r in snapshot)
            _rows.Add(new RewriteRow
            {
                Location = r.Location,
                OriginalText = r.OriginalText,
                NewText = r.NewText,
                Accepted = accepted,
                Source = r.Source,
            });
        UpdateCount();
    }

    private void UpdateCount()
    {
        int accepted = _rows.Count(r => r.Accepted);
        CountLabel.Text = L.F("RewritePreview_Count_Format", accepted, _rows.Count);
    }

    private void ApplyAndClose()
    {
        Result = _rows
            .Where(r => r.Accepted)
            .Select(r => r.Source with { Accepted = true })
            .ToList();
        Close();
    }
}

public sealed class RewriteRow
{
    public string Location { get; set; } = "";
    public string OriginalText { get; set; } = "";
    public string NewText { get; set; } = "";
    public bool Accepted { get; set; }
    public BodyTextEdit Source { get; set; } = null!;
}
