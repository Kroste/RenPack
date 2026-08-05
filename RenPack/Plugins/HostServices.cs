using Avalonia.Controls;
using NLog;

namespace RenPack.Plugins;

/// <summary>Konkrete <see cref="IHostServices"/>-Impl die vom
/// <see cref="PluginLoader"/> pro Plugin erzeugt wird. Registriert
/// Menu-Items in einem gemeinsamen <see cref="PluginMenuRegistry"/>
/// und Tabs im <see cref="PluginTabRegistry"/>, die das MainWindow
/// beobachtet.</summary>
internal sealed class HostServices : IHostServices
{
    public HostServices(string pluginName, Window mainWindow,
        ISecretProtection secrets, PluginMenuRegistry menus, PluginTabRegistry tabs)
    {
        Logger = LogManager.GetLogger($"Plugin.{pluginName}");
        Secrets = secrets;
        MainWindow = mainWindow;
        _menus = menus;
        _tabs = tabs;
        _pluginName = pluginName;

        string userConfig = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        PluginDataDir = Path.Combine(userConfig, "RenPack", "plugins",
            SanitizeName(pluginName));
        Directory.CreateDirectory(PluginDataDir);
    }

    private readonly PluginMenuRegistry _menus;
    private readonly PluginTabRegistry _tabs;
    private readonly string _pluginName;

    public Logger Logger { get; }
    public string PluginDataDir { get; }
    public ISecretProtection Secrets { get; }
    public Window MainWindow { get; }

    public void RegisterToolMenuItem(string icon, string label, Func<Task> onClick)
        => _menus.Register(new PluginMenuItem(_pluginName, icon, label, onClick));

    public void RegisterTab(string icon, string label, Func<Control> contentFactory)
        => _tabs.Register(new PluginTab(_pluginName, icon, label, contentFactory));

    /// <summary>Path-safe Version des Plugin-Namens (Leerzeichen weg,
    /// nur ASCII-Buchstaben/Ziffern/Underscore).</summary>
    private static string SanitizeName(string name)
    {
        var chars = name.Select(c =>
            char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray();
        return new string(chars);
    }
}

/// <summary>Zentrale Registry aller Plugin-Menu-Items. Von den
/// HostServices-Instanzen befuellt, vom MainWindow-ViewModel via
/// ObservableCollection gebunden.</summary>
public sealed class PluginMenuRegistry
{
    private readonly List<PluginMenuItem> _items = new();
    public IReadOnlyList<PluginMenuItem> Items => _items;

    public event EventHandler? Changed;

    public void Register(PluginMenuItem item)
    {
        _items.Add(item);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Ein vom Plugin registrierter Menu-Item.
/// <see cref="OnClick"/> laeuft auf dem UI-Thread.</summary>
public sealed record PluginMenuItem(
    string PluginName,
    string Icon,
    string Label,
    Func<Task> OnClick);

/// <summary>Zentrale Registry aller Plugin-Tabs. Analog zu
/// <see cref="PluginMenuRegistry"/>: von HostServices befuellt, vom
/// MainWindow ueber Changed-Event beobachtet.</summary>
public sealed class PluginTabRegistry
{
    private readonly List<PluginTab> _items = new();
    public IReadOnlyList<PluginTab> Items => _items;

    public event EventHandler? Changed;

    public void Register(PluginTab tab)
    {
        _items.Add(tab);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Ein vom Plugin registrierter Tab im MainWindow.
/// <see cref="ContentFactory"/> wird lazy beim ersten Tab-Selektieren
/// aufgerufen und liefert das Root-Control der Plugin-UI.</summary>
public sealed record PluginTab(
    string PluginName,
    string Icon,
    string Label,
    Func<Control> ContentFactory);
