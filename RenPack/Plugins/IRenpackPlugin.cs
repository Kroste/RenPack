namespace RenPack.Plugins;

/// <summary>Contract fuer RenPack-Plugins. Plugins sind eigene .NET-
/// Assemblies (`.dll`) in einem <c>plugins/</c>-Ordner die per
/// <see cref="PluginLoader"/> beim App-Start entdeckt und initialisiert
/// werden.
///
/// **Plugin-Deployment:** eine Plugin-Assembly muss RenPack als
/// PackageReference haben (fuer die Contracts hier). Beim Build sollten
/// alle Nicht-Framework-Assemblies mit rausfallen; ins <c>plugins/</c>
/// gehoert nur die Plugin-DLL selbst plus fremde Dependencies (nicht
/// RenPack.dll — die kommt aus dem Host-Prozess).
///
/// **Lifecycle:** Host ruft <see cref="Initialize"/> nach Discovery auf.
/// Beim App-Shutdown wird <see cref="Dispose"/> aufgerufen — Plugin
/// kann dort Timer stoppen, offene HttpClient dispose'n etc.
///
/// **Fehler-Handling:** wirft <c>Initialize</c> oder der Konstruktor
/// eine Exception, wird das Plugin uebersprungen und ein Warning
/// geloggt — die App startet trotzdem sauber weiter.</summary>
public interface IRenpackPlugin : IDisposable
{
    /// <summary>Anzeigename im Plugin-Manager. Sollte kurz sein
    /// (z.B. „F95zone Update Checker").</summary>
    string Name { get; }

    /// <summary>Semver-Version des Plugins. Nur zur Anzeige, RenPack
    /// macht keine Kompatibilitaets-Pruefung.</summary>
    string Version { get; }

    /// <summary>Optional — Autor/Team.</summary>
    string? Author => null;

    /// <summary>Optional — ein-Satz-Beschreibung fuer den Plugin-Manager.</summary>
    string? Description => null;

    /// <summary>Wird nach der Assembly-Ladung und Ctor aufgerufen.
    /// Plugin kann hier Menu-Items registrieren, Timer starten,
    /// Config aus <see cref="IHostServices.PluginDataDir"/> lesen.</summary>
    void Initialize(IHostServices host);
}
