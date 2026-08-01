using System.Text.Json;
using Avalonia.Controls;
using NLog;

namespace RenPack.Services;

/// <summary>
/// Persistiert Fenster-Groesse und -Position pro Fenster-Klasse in
/// einer kleinen JSON-Datei neben <c>settings.json</c>. Beim naechsten
/// Start restauriert das Fenster seine letzte Position — kein
/// Zurueckspringen auf die Default-Groesse.
///
/// <b>Verwendung:</b> im Fenster-ctor <c>WindowStateService.Attach(this)</c>
/// aufrufen — das lauscht auf Move/Resize/Close und persistiert
/// beim Schliessen; beim Attach wird direkt der zuletzt gespeicherte
/// State restauriert (falls vorhanden).
/// </summary>
public sealed class WindowStateService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _configPath;
    private Dictionary<string, PersistedWindowState> _cache;

    public WindowStateService() : this(DefaultConfigPath()) { }

    public WindowStateService(string configPath)
    {
        _configPath = configPath;
        _cache = Load();
    }

    public void Attach(Window window)
    {
        string key = window.GetType().Name;
        // Restore vor dem Rendering — sonst blitzt kurz die Default-Groesse.
        if (_cache.TryGetValue(key, out var state))
            ApplyState(window, state);

        window.Closing += (_, _) => Save(window, key);
    }

    private static void ApplyState(Window window, PersistedWindowState s)
    {
        // Position: nur zuweisen wenn plausibel (Fenster nicht ausserhalb
        // aller Bildschirme). Avalonia's Screens sind zum Fenster-ctor-
        // Zeitpunkt oft noch null — daher Grenzwertpruefung erst spaeter,
        // wir vertrauen dem gespeicherten Wert.
        if (s.Width > 100 && s.Height > 100)
        {
            window.Width = s.Width;
            window.Height = s.Height;
        }
        if (s.X.HasValue && s.Y.HasValue)
            window.Position = new Avalonia.PixelPoint(s.X.Value, s.Y.Value);
        if (s.Maximized) window.WindowState = WindowState.Maximized;
    }

    private void Save(Window window, string key)
    {
        try
        {
            var maximized = window.WindowState == WindowState.Maximized;
            // Bei Maximiert die Original-Groesse behalten (RestoreBounds
            // hat Avalonia so nicht — die aktuelle Groesse ist bei
            // maximized der ganze Screen, das wollen wir nicht persistieren).
            var w = maximized ? _cache.GetValueOrDefault(key)?.Width ?? window.Width : window.Width;
            var h = maximized ? _cache.GetValueOrDefault(key)?.Height ?? window.Height : window.Height;
            var x = maximized ? _cache.GetValueOrDefault(key)?.X : (int?)window.Position.X;
            var y = maximized ? _cache.GetValueOrDefault(key)?.Y : (int?)window.Position.Y;

            _cache[key] = new PersistedWindowState(w, h, x, y, maximized);
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            var tmp = _configPath + ".tmp";
            using (var stream = File.Create(tmp))
                JsonSerializer.Serialize(stream, _cache, JsonOptions);
            File.Move(tmp, _configPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Fenster-State konnte nicht gespeichert werden ({key})", key);
        }
    }

    private Dictionary<string, PersistedWindowState> Load()
    {
        try
        {
            if (!File.Exists(_configPath))
                return new Dictionary<string, PersistedWindowState>(StringComparer.Ordinal);
            using var stream = File.OpenRead(_configPath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, PersistedWindowState>>(stream, JsonOptions);
            return dict ?? new Dictionary<string, PersistedWindowState>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Fenster-State-Datei unlesbar, ignoriere");
            return new Dictionary<string, PersistedWindowState>(StringComparer.Ordinal);
        }
    }

    private static string DefaultConfigPath()
    {
        string baseDir = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(baseDir, "RenPack", "windows.json");
    }

    public sealed record PersistedWindowState(double Width, double Height, int? X, int? Y, bool Maximized);
}
