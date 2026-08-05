using System.Reflection;
using System.Runtime.Loader;
using NLog;

namespace RenPack.Plugins;

/// <summary>Sucht und laedt <see cref="IRenpackPlugin"/>-Implementierungen
/// aus zwei Standard-Ordnern:
/// <list type="bullet">
///   <item><c>plugins/</c> neben der App-Exe (fuer Bundle-Deployment)</item>
///   <item><c>$XDG_CONFIG_HOME/RenPack/plugins/</c> bzw.
///     <c>%APPDATA%/RenPack/plugins/</c> (fuer User-installierte Plugins)</item>
/// </list>
///
/// **Assembly-Isolation**: pro Plugin ein eigener
/// <see cref="AssemblyLoadContext"/>, damit Plugins voneinander getrennte
/// Dependency-Versionen haben koennen. RenPack-Assemblies selbst laufen
/// im Default-Context — der PluginContext delegiert die dorthin, wenn
/// der Type-Name matcht (sonst wuerde ein Plugin ein zweites Exemplar
/// von <see cref="IRenpackPlugin"/> laden und der Cast zur Host-Instanz
/// klappt nicht).
///
/// **Robustheit**: eine kaputte Plugin-DLL blockiert nicht den App-
/// Start. Fehler werden geloggt, das Plugin uebersprungen.</summary>
public sealed class PluginLoader : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly List<LoadedPlugin> _loaded = new();
    public IReadOnlyList<LoadedPlugin> Loaded => _loaded;

    public IEnumerable<string> PluginDirectories
    {
        get
        {
            var appDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetEntryAssembly()?.Location
                ?? AppContext.BaseDirectory);
            if (!string.IsNullOrEmpty(appDir))
                yield return Path.Combine(appDir, "plugins");

            string userConfig = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                  ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            yield return Path.Combine(userConfig, "RenPack", "plugins");
        }
    }

    /// <summary>Discovery-Pass: sucht alle <c>*.dll</c> in den Plugin-
    /// Ordnern und versucht sie als <see cref="IRenpackPlugin"/> zu
    /// laden. Init wird pro Plugin mit einem eigenen
    /// <see cref="IHostServices"/>-Wrapper aufgerufen. Rueckgabe:
    /// Anzahl erfolgreich geladener Plugins.</summary>
    public int LoadAll(Func<string, IHostServices> hostFactory)
    {
        foreach (var dir in PluginDirectories)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var dllPath in Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
                TryLoadPlugin(dllPath, hostFactory);
        }
        Log.Info("PluginLoader: {n} Plugin(s) geladen", _loaded.Count);
        return _loaded.Count;
    }

    private void TryLoadPlugin(string dllPath, Func<string, IHostServices> hostFactory)
    {
        try
        {
            var ctx = new PluginLoadContext(dllPath);
            var asm = ctx.LoadFromAssemblyPath(dllPath);
            var pluginType = asm.GetTypes()
                .FirstOrDefault(t => typeof(IRenpackPlugin).IsAssignableFrom(t)
                    && !t.IsAbstract && !t.IsInterface);
            if (pluginType is null)
            {
                Log.Debug("Kein IRenpackPlugin in {dll} — ueberspringe", dllPath);
                ctx.Unload();
                return;
            }
            var plugin = (IRenpackPlugin)Activator.CreateInstance(pluginType)!;
            var host = hostFactory(plugin.Name);
            plugin.Initialize(host);
            _loaded.Add(new LoadedPlugin(plugin, dllPath, ctx));
            Log.Info("Plugin geladen: {name} v{version} ({path})",
                plugin.Name, plugin.Version, dllPath);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Plugin-Load fehlgeschlagen: {path}", dllPath);
        }
    }

    public void Dispose()
    {
        foreach (var p in _loaded)
        {
            try { p.Plugin.Dispose(); }
            catch (Exception ex) { Log.Warn(ex, "Plugin-Dispose fehlgeschlagen: {name}", p.Plugin.Name); }
            try { p.LoadContext.Unload(); } catch { }
        }
        _loaded.Clear();
    }

    /// <summary>Pro-Plugin AssemblyLoadContext. Delegiert alles was der
    /// Default-Context schon hat (RenPack, System.*, Avalonia.*) an
    /// Default zurueck — sonst wuerde IRenpackPlugin doppelt geladen
    /// und der Cast in <see cref="TryLoadPlugin"/> wuerfe
    /// InvalidCastException.</summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginPath)
            : base(name: $"Plugin_{Path.GetFileNameWithoutExtension(pluginPath)}",
                   isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Wenn Default-Context die Assembly schon geladen hat
            // (RenPack.dll, Avalonia.*, System.*), NICHT neu laden.
            var alreadyLoaded = Default.Assemblies
                .FirstOrDefault(a => a.GetName().Name == assemblyName.Name);
            if (alreadyLoaded is not null) return null;

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}

/// <summary>Eintrag im Plugin-Manager: der Plugin selbst + wo er
/// herkommt + sein LoadContext (fuer Unload beim Shutdown).</summary>
public sealed record LoadedPlugin(
    IRenpackPlugin Plugin,
    string SourcePath,
    AssemblyLoadContext LoadContext);
