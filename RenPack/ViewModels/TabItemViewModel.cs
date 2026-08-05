using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RenPack.ViewModels;

/// <summary>Ein Tab im MainWindow. Erster Tab ist immer der
/// „Archiv"-Tab (wird speziell behandelt — sein Content ist die
/// bisherige XAML-Struktur, nicht ein Control aus einer Factory).
/// Weitere Tabs kommen von Plugins ueber
/// <c>IHostServices.RegisterTab</c>.
///
/// **Lazy content**: Plugin-UI wird erst beim ersten Tab-Klick
/// gebaut. Danach gecached, damit State (Scroll-Position, Selection
/// etc.) beim Zurueckwechseln erhalten bleibt.</summary>
public sealed partial class TabItemViewModel : ObservableObject
{
    private readonly System.Func<Control>? _factory;
    private Control? _cachedContent;

    public TabItemViewModel(string icon, string label, bool isArchiveTab,
        System.Func<Control>? factory)
    {
        Icon = icon;
        Label = label;
        IsArchiveTab = isArchiveTab;
        _factory = factory;
    }

    public string Icon { get; }
    public string Label { get; }
    public bool IsArchiveTab { get; }

    /// <summary>Baut das Plugin-Control lazy beim ersten Aufruf,
    /// gibt danach die gecachte Instanz zurueck. Fuer den Archiv-Tab
    /// gibt es keinen Content (der wird direkt im XAML gerendert).</summary>
    public Control? EnsureContent()
    {
        if (IsArchiveTab || _factory is null) return null;
        return _cachedContent ??= _factory();
    }
}
