using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RenPack.Services;

namespace RenPack.ViewModels;

/// <summary>Zeile in der Save-Variablen-Tabelle. Wert ist ab v0.3 editierbar,
/// solange <see cref="IsEditable"/> zutrifft (einfache Typen).</summary>
public sealed partial class SaveVariableViewModel : ObservableObject
{
    private readonly string _originalValue;

    public SaveVariableViewModel(SaveVariable v)
    {
        Name = v.Name;
        TypeName = v.TypeName;
        _originalValue = v.Value;
        _editableValue = v.Value;
        IsInternal = v.IsInternal;
        // Simple types: direkt editierbar. list/dict/tuple: nur wenn der
        // Value als Python-Literal parsebar ist (siehe ValueDisplay in
        // RenpySaveService — der Display-Text ist bereits das Literal).
        IsEditable = TypeName switch
        {
            "int" or "float" or "str" or "bool" or "None" => true,
            "list" or "dict" or "tuple" => PythonLiteral.TryParse(v.Value, out _),
            _ => false,
        };
    }

    public string Name { get; }
    public string TypeName { get; }
    public bool IsInternal { get; }
    public bool IsEditable { get; }
    public string OriginalValue => _originalValue;

    /// <summary>Der aktuell in der UI angezeigte / editierte Wert (Text-Form).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _editableValue;

    public bool IsDirty => IsEditable && !string.Equals(EditableValue, _originalValue, StringComparison.Ordinal);

    /// <summary>Menschenlesbare Beschreibung der Variable (aus KI-Übersetzung).
    /// Leer, wenn (noch) keine Übersetzung vorliegt.</summary>
    [ObservableProperty] private string _description = "";

    /// <summary>Pack I v0.9: User-Bookmark. Favorisierte Vars werden per
    /// Save-Datei-Pfad im <see cref="FavoriteVarsService"/> persistiert
    /// und in der Anzeige (wenn <c>FavoritesFirst</c> aktiv) an den Anfang
    /// der Tabelle gezogen.</summary>
    [ObservableProperty] private bool _isFavorite;

    /// <summary>Konvertiert den Anzeige-Text in einen .NET-Wert passend zum Typ.
    /// Wirft <see cref="FormatException"/> bei ungültiger Eingabe.</summary>
    public object? ParseEditedValue() => TypeName switch
    {
        "bool" => EditableValue.Equals("True", StringComparison.OrdinalIgnoreCase),
        "int" => long.Parse(EditableValue, CultureInfo.InvariantCulture),
        "float" => double.Parse(EditableValue, CultureInfo.InvariantCulture),
        "str" => EditableValue,
        "None" => null,
        "list" or "dict" or "tuple" => PythonLiteral.Parse(EditableValue),
        _ => throw new NotSupportedException($"Typ {TypeName} ist nicht editierbar."),
    };
}
