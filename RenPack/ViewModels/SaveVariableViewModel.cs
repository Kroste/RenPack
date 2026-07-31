using CommunityToolkit.Mvvm.ComponentModel;
using RenPack.Services;

namespace RenPack.ViewModels;

/// <summary>Zeile in der Save-Variablen-Tabelle. Read-only in v0.2.</summary>
public sealed partial class SaveVariableViewModel : ObservableObject
{
    public SaveVariableViewModel(SaveVariable v)
    {
        Name = v.Name;
        TypeName = v.TypeName;
        Value = v.Value;
        IsInternal = v.IsInternal;
    }

    public string Name { get; }
    public string TypeName { get; }
    public string Value { get; }
    public bool IsInternal { get; }
}
