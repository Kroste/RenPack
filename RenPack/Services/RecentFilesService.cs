using System.Text.Json;
using NLog;

namespace RenPack.Services;

/// <summary>Persistente MRU-Listen (Most Recently Used) pro Kategorie —
/// Archive, Save-Dateien, Decompile-Ordner. Gespeichert unter
/// <c>recent.json</c> neben <c>settings.json</c>. Max 8 Eintraege pro
/// Kategorie, Deduplizierung, aktueller Eintrag wandert an Position 0.
/// Nicht mehr existierende Pfade werden beim Lesen automatisch entfernt.
/// </summary>
public sealed class RecentFilesService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private const int MaxItems = 8;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _configPath;
    private RecentData _data;

    public RecentFilesService() : this(DefaultConfigPath()) { }
    public RecentFilesService(string configPath)
    {
        _configPath = configPath;
        _data = Load();
    }

    /// <summary>Wird bei jeder Aenderung gefeuert. UI-ViewModels
    /// abonnieren und aktualisieren ihre gebundenen Listen.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<string> Archives => Existing(_data.Archives);
    public IReadOnlyList<string> Saves => Existing(_data.Saves);
    public IReadOnlyList<string> DecompileFolders => ExistingDirs(_data.DecompileFolders);
    public IReadOnlyList<string> ModGameFolders => ExistingDirs(_data.ModGameFolders);

    public void AddArchive(string path) => Add(_data.Archives, path);
    public void AddSave(string path) => Add(_data.Saves, path);
    public void AddDecompileFolder(string path) => Add(_data.DecompileFolders, path);
    public void AddModGameFolder(string path) => Add(_data.ModGameFolders, path);

    private void Add(List<string> list, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        while (list.Count > MaxItems) list.RemoveAt(list.Count - 1);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static IReadOnlyList<string> Existing(List<string> list)
        => list.Where(File.Exists).ToList();

    private static IReadOnlyList<string> ExistingDirs(List<string> list)
        => list.Where(Directory.Exists).ToList();

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            var tmp = _configPath + ".tmp";
            using (var stream = File.Create(tmp))
                JsonSerializer.Serialize(stream, _data, JsonOptions);
            File.Move(tmp, _configPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Recent-Files konnten nicht gespeichert werden");
        }
    }

    private RecentData Load()
    {
        try
        {
            if (!File.Exists(_configPath)) return new RecentData();
            using var stream = File.OpenRead(_configPath);
            return JsonSerializer.Deserialize<RecentData>(stream, JsonOptions) ?? new RecentData();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Recent-Files-Datei unlesbar");
            return new RecentData();
        }
    }

    private static string DefaultConfigPath()
    {
        string baseDir = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(baseDir, "RenPack", "recent.json");
    }

    private sealed class RecentData
    {
        public List<string> Archives { get; set; } = [];
        public List<string> Saves { get; set; } = [];
        public List<string> DecompileFolders { get; set; } = [];
        public List<string> ModGameFolders { get; set; } = [];
    }
}
