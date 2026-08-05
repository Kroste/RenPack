using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace RenPack.Plugins;

/// <summary>Services die Plugins vom RenPack-Host bekommen. Bewusst
/// klein gehalten — jede zusaetzliche API ist ein API-Vertrag den wir
/// nicht mehr brechen sollten. Erweitern nur wenn ein konkretes Plugin
/// es braucht.</summary>
public interface IHostServices
{
    /// <summary>NLog-Logger, benannt nach dem Plugin (Log-Zeilen sind
    /// so filterbar pro Plugin).</summary>
    NLog.Logger Logger { get; }

    /// <summary>Persistenter Config-Ordner fuer dieses Plugin —
    /// <c>$XDG_CONFIG_HOME/RenPack/plugins/&lt;PluginName&gt;/</c>
    /// bzw. <c>%APPDATA%/RenPack/plugins/&lt;PluginName&gt;/</c>. Wird
    /// bei Bedarf angelegt.</summary>
    string PluginDataDir { get; }

    /// <summary>Encrypted Secret-Storage (DPAPI/AES) — fuer API-Keys,
    /// Session-Cookies, Passworter. Nie im Klartext, nie loggen.</summary>
    ISecretProtection Secrets { get; }

    /// <summary>Das Haupt-Fenster als Owner fuer Modal-Dialoge (Plugin
    /// oeffnet eigene Windows via <c>ShowDialog(host.MainWindow)</c>).</summary>
    Window MainWindow { get; }

    /// <summary>Registriert einen Menu-Item in der Plugins-Sektion der
    /// MainWindow-Toolbar. Icon ist ein Emoji-String (z.B. „🌐"),
    /// label lokalisierbar durch Plugin selbst. onClick laeuft auf dem
    /// UI-Thread und kann Dialoge oeffnen, Files lesen etc.</summary>
    void RegisterToolMenuItem(string icon, string label, Func<Task> onClick);
}
