using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace RenPack.Localization;

/// <summary>
/// Markup-Extension für kompaktes XAML-Binding auf lokalisierte Strings:
/// <c>Text="{loc:Tr OpenArchive}"</c>. Erzeugt intern einen
/// <see cref="LocalizedString"/>-Wrapper und bindet an dessen
/// <see cref="LocalizedString.Value"/>-Property. Sobald der
/// <see cref="LocalizationService"/> die Sprache wechselt, feuert
/// jeder Wrapper ein regulaeres <c>PropertyChanged</c> — alle Bindings
/// in allen Fenstern refreshen live.
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public TrExtension() { }
    public TrExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        // Wrapper aus dem statischen Cache — nicht pro Binding neu
        // erzeugen! Avalonia haelt Binding.Source nicht dauerhaft
        // stark; ein frisch erzeugter Wrapper wuerde nach dem ersten
        // Rendering GC'd, und die Sprachwechsel-Notification liefe ins
        // Leere. Der Cache haelt jeden Wrapper fuer die App-Lebensdauer.
        return new Binding(nameof(LocalizedString.Value))
        {
            Source = LocalizedString.Get(Key),
            Mode = BindingMode.OneWay,
        };
    }
}
