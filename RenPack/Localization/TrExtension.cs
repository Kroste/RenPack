using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace RenPack.Localization;

/// <summary>
/// Markup-Extension für kompaktes XAML-Binding auf lokalisierte Strings:
/// <c>Text="{loc:Tr OpenArchive}"</c>. Erzeugt intern ein Binding an
/// <see cref="LocalizationService.Instance"/>'s Indexer — sobald der
/// Service <c>PropertyChanged("Item[]")</c> feuert, aktualisiert sich das
/// Ziel live, ohne dass das Fenster neu geladen werden muss.
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public TrExtension() { }
    public TrExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return new Binding($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay,
        };
    }
}
