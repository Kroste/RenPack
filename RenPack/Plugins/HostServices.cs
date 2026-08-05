using Avalonia.Controls;
using NLog;

namespace RenPack.Plugins;

/// <summary>Konkrete <see cref="IHostServices"/>-Impl die vom
/// <see cref="PluginLoader"/> pro Plugin erzeugt wird. Registriert
/// Menu-Items in einem gemeinsamen <see cref="PluginMenuRegistry"/>,
/// den das MainWindow beobachtet.</summary>
internal sealed class HostServices : IHostServices
{
    public HostServices(string pluginName, Window mainWindow,
        ISecretProtection secrets, PluginMenuRegistry registry)
    {
        Logger = LogManager.GetLogger($"Plugin.{pluginName}");
        Secrets = secrets;
        MainWindow = mainWindow;
        _registry = registry;
        _pluginName = pluginName;

        string userConfig = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        PluginDataDir = Path.Combine(userConfig, "RenPack", "plugins",
            SanitizeName(pluginName));
        Directory.CreateDirectory(PluginDataDir);
    }

    private readonly PluginMenuRegistry _registry;
    private readonly string _pluginName;

    public Logger Logger { get; }
    public string PluginDataDir { get; }
    public ISecretProtection Secrets { get; }
    public Window MainWindow { get; }

    public void RegisterToolMenuItem(string icon, string label, Func<Task> onClick)
        => _registry.Register(new PluginMenuItem(_pluginName, icon, label, onClick));

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
