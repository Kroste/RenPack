using System.Text.Json;
using NLog;

namespace RenPack.Services;

/// <summary>Persistiert User-markierte Favoriten-Variablen pro Save-Datei-
/// Pfad. Wird vom Save-Editor konsumiert um markierte Vars beim naechsten
/// Laden derselben Datei wieder als Bookmarks anzuzeigen.
///
/// **Warum pro Save-Pfad und nicht pro Spiel?** Der Pfad ist der einzige
/// stabile Key den wir haben — Spiel-Erkennung bräuchte weitere Heuristik
/// (Save-Struktur analysieren) und ist unzuverlaessig. Der User arbeitet
/// meistens iterativ am selben Save; die Path-Semantik reicht.</summary>
public sealed class FavoriteVarsService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _configPath;
    private Dictionary<string, List<string>> _favoritesByPath;

    public FavoriteVarsService() : this(DefaultConfigPath()) { }
    public FavoriteVarsService(string configPath)
    {
        _configPath = configPath;
        _favoritesByPath = Load();
    }

    public IReadOnlySet<string> GetFavorites(string savePath)
    {
        return _favoritesByPath.TryGetValue(savePath, out var list)
            ? new HashSet<string>(list, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    public void SetFavorites(string savePath, IEnumerable<string> favoriteVarNames)
    {
        var list = favoriteVarNames.Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (list.Count == 0)
            _favoritesByPath.Remove(savePath);
        else
            _favoritesByPath[savePath] = list;
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            var tmp = _configPath + ".tmp";
            using (var stream = File.Create(tmp))
                JsonSerializer.Serialize(stream, _favoritesByPath, JsonOptions);
            File.Move(tmp, _configPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Favorites-Datei konnte nicht gespeichert werden");
        }
    }

    private Dictionary<string, List<string>> Load()
    {
        try
        {
            if (!File.Exists(_configPath))
                return new Dictionary<string, List<string>>(StringComparer.Ordinal);
            using var stream = File.OpenRead(_configPath);
            return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(stream, JsonOptions)
                ?? new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Favorites-Datei unlesbar");
            return new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }
    }

    private static string DefaultConfigPath()
    {
        string baseDir = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(baseDir, "RenPack", "save_favorites.json");
    }
}
