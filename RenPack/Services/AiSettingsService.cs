using System.Text.Json;
using NLog;

namespace RenPack.Services;

/// <summary>
/// Lädt und speichert die KI-Einstellungen unter
/// <c>$XDG_CONFIG_HOME/RenPack/settings.json</c> (Linux) bzw.
/// <c>%APPDATA%/RenPack/settings.json</c> (Windows). Atomares Schreiben via
/// <c>.tmp</c>+<see cref="File.Move"/>, damit ein Absturz mitten im Schreiben
/// die alte Datei nicht zerstört.
/// </summary>
public sealed class AiSettingsService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _configPath;
    private AiSettings _current;

    public AiSettingsService() : this(DefaultConfigPath()) { }

    public AiSettingsService(string configPath)
    {
        _configPath = configPath;
        _current = Load();
    }

    public AiSettings Current => _current;

    public void Update(AiSettings settings)
    {
        _current = settings;
        Save(settings);
    }

    private AiSettings Load()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                Log.Info("Keine KI-Konfigurationsdatei — verwende Defaults ({path})", _configPath);
                return AiSettings.Default;
            }
            using var stream = File.OpenRead(_configPath);
            var loaded = JsonSerializer.Deserialize<AiSettings>(stream, JsonOptions);
            return loaded ?? AiSettings.Default;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "KI-Konfigurationsdatei unlesbar, verwende Defaults");
            return AiSettings.Default;
        }
    }

    private void Save(AiSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            var tmp = _configPath + ".tmp";
            using (var stream = File.Create(tmp))
                JsonSerializer.Serialize(stream, settings, JsonOptions);
            File.Move(tmp, _configPath, overwrite: true);
            Log.Info("KI-Konfiguration gespeichert: {path}", _configPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "KI-Konfiguration konnte nicht gespeichert werden");
        }
    }

    private static string DefaultConfigPath()
    {
        string baseDir;
        if (OperatingSystem.IsWindows())
        {
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }
        else
        {
            baseDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        return Path.Combine(baseDir, "RenPack", "settings.json");
    }
}
