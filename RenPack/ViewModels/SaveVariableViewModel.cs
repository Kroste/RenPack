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
        IsEditable = TypeName is "int" or "float" or "str" or "bool" or "None";
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

    /// <summary>Konvertiert den Anzeige-Text in einen .NET-Wert passend zum Typ.
    /// Wirft <see cref="FormatException"/> bei ungültiger Eingabe.</summary>
    public object? ParseEditedValue() => TypeName switch
    {
        "bool" => EditableValue.Equals("True", StringComparison.OrdinalIgnoreCase),
        "int" => long.Parse(EditableValue, CultureInfo.InvariantCulture),
        "float" => double.Parse(EditableValue, CultureInfo.InvariantCulture),
        "str" => EditableValue,
        "None" => null,
        _ => throw new NotSupportedException($"Typ {TypeName} ist nicht editierbar."),
    };
}
