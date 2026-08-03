using System.Collections.ObjectModel;
using RenPack.Services.Modding;

namespace RenPack.Views;

/// <summary>Modaler Dialog zum Konfigurieren der Character-Renames.
/// Zeigt eine DataGrid mit einer Zeile pro erkanntem Character. Der User
/// traegt in die „New Name"-Spalte den gewuenschten neuen Anzeigenamen
/// ein — leer lassen = keine Aenderung.</summary>
public partial class RenameConfigWindow : ChromeWindow
{
    private readonly ObservableCollection<RenameRow> _rows = new();
    private readonly ObservableCollection<RelationRow> _relations = new();

    /// <summary>Wird gesetzt wenn User Apply klickt, bleibt <c>null</c> bei
    /// Cancel/Close. Aufrufer prueft nach <see cref="ShowDialogAsync"/>.</summary>
    public RenameConfig? Result { get; private set; }

    /// <summary>True wenn User zusaetzlich zum Character-Rename auch die
    /// KI-basierte Body-Text-Umschreibung will (E4b). Der Aufrufer muss dann
    /// den Rewriter aufrufen und das Preview-Ergebnis zurueck-mergen.</summary>
    public bool UseAiRewrite { get; private set; }

    public RenameConfigWindow()
    {
        InitializeComponent();
        MappingsGrid.ItemsSource = _rows;
        RelationsGrid.ItemsSource = _relations;
        OkButton.Click += (_, _) => ApplyAndClose();
        CancelButton.Click += (_, _) => { Result = null; Close(); };
        AddRelationButton.Click += (_, _) => _relations.Add(new RelationRow());
        RemoveRelationButton.Click += (_, _) =>
        {
            if (RelationsGrid.SelectedItem is RelationRow r) _relations.Remove(r);
        };
    }

    /// <summary>Populate die DataGrid mit den erkannten Characters.</summary>
    public void Load(IReadOnlyList<RpyCharacter> characters)
    {
        _rows.Clear();
        foreach (var c in characters.OrderBy(c => c.VarName, StringComparer.Ordinal))
        {
            _rows.Add(new RenameRow
            {
                VarName = c.VarName,
                OriginalName = c.DisplayName,
                NewName = "",
            });
        }
    }

    private void ApplyAndClose()
    {
        // Nur Zeilen mit non-leerem NewName ins Result nehmen.
        var dict = _rows
            .Where(r => !string.IsNullOrWhiteSpace(r.NewName)
                        && r.NewName.Trim() != r.OriginalName)
            .ToDictionary(r => r.VarName, r => r.NewName.Trim(), StringComparer.Ordinal);
        // Beziehungs-Mappings: beide Spalten muessen non-leer sein, und
        // der Wert darf nicht identisch zum Key sein (sonst kein Effekt).
        var relations = _relations
            .Where(r => !string.IsNullOrWhiteSpace(r.From) && !string.IsNullOrWhiteSpace(r.To)
                        && r.From.Trim() != r.To.Trim())
            .ToDictionary(r => r.From.Trim(), r => r.To.Trim(), StringComparer.Ordinal);
        Result = new RenameConfig(
            Mappings: dict,
            BodyTextEdits: null,
            RelationMappings: relations.Count > 0 ? relations : null);
        UseAiRewrite = AiRewriteCheckbox.IsChecked ?? false;
        Close();
    }
}

/// <summary>DataGrid-Zeilen-Model. Public damit das XAML-Binding
/// (<c>x:CompileBindings</c>) den Typ findet.</summary>
public sealed class RenameRow
{
    public string VarName { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public string NewName { get; set; } = "";
}

/// <summary>Zeilen-Model fuer die Beziehungswoerter-Grid (E4c). Freies
/// From→To-Mapping; wird vom KI-Rewriter zusammen mit den Character-
/// Namen an die KI gegeben.</summary>
public sealed class RelationRow
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}
