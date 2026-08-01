using System.Text.Json;
using NLog;

namespace RenPack.Services;

/// <summary>
/// Lädt und speichert die KI-Einstellungen unter
/// <c>$XDG_CONFIG_HOME/RenPack/settings.json</c> (Linux) bzw.
/// <c>%APPDATA%/RenPack/settings.json</c> (Windows). Atomares Schreiben via
/// <c>.tmp</c>+<see cref="File.Move"/>. API-Keys werden über
/// <see cref="SecretProtection"/> vor dem Persistieren verschlüsselt und
/// beim Laden entschlüsselt — die JSON-Datei enthält niemals Klartext-Keys.
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

    /// <summary>Wird nach jedem <see cref="Update"/> gefeuert. ViewModels, die
    /// gebundene Command-CanExecute-Werte aus den Settings ableiten, abonnieren
    /// und rufen <c>NotifyCanExecuteChanged</c> auf — sonst bleibt der Zustand
    /// stale, wenn der Nutzer den Provider erst nach dem Öffnen des Feature-
    /// Fensters konfiguriert.</summary>
    public event EventHandler? SettingsChanged;

    public void Update(AiSettings settings)
    {
        _current = settings;
        Save(settings);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Persistenz-DTO ----------------------------------------------------
    //
    // Auf Disk liegt eine eigene Struktur, in der die Keys als "v1:base64..."
    // gespeichert sind. Beim Laden/Speichern konvertieren wir zwischen DTO
    // und dem Runtime-Record AiSettings hin und her.

    private sealed record PersistedConfig(string Endpoint, string Model, string? ApiKeyProtected);

    private sealed record PersistedSettings(
        AiProviderType Provider,
        string TargetLanguage,
        string? UiCulture, // Nullable, damit alte Configs ohne Feld lesbar bleiben
        PersistedConfig Ollama,
        PersistedConfig Anthropic,
        PersistedConfig OpenAi,
        PersistedConfig Gemini,
        PersistedConfig Mistral,
        PersistedConfig OpenAiCompatible);

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
            var dto = JsonSerializer.Deserialize<PersistedSettings>(stream, JsonOptions);
            return dto is null ? AiSettings.Default : FromPersisted(dto);
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
            var dto = ToPersisted(settings);
            var tmp = _configPath + ".tmp";
            using (var stream = File.Create(tmp))
                JsonSerializer.Serialize(stream, dto, JsonOptions);
            File.Move(tmp, _configPath, overwrite: true);
            Log.Info("KI-Konfiguration gespeichert: {path}", _configPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "KI-Konfiguration konnte nicht gespeichert werden");
        }
    }

    private static PersistedSettings ToPersisted(AiSettings s) => new(
        Provider: s.Provider,
        TargetLanguage: s.TargetLanguage,
        UiCulture: s.UiCulture,
        Ollama:            ToPersisted(s.Ollama),
        Anthropic:         ToPersisted(s.Anthropic),
        OpenAi:            ToPersisted(s.OpenAi),
        Gemini:            ToPersisted(s.Gemini),
        Mistral:           ToPersisted(s.Mistral),
        OpenAiCompatible:  ToPersisted(s.OpenAiCompatible));

    private static AiSettings FromPersisted(PersistedSettings p) => new(
        Provider: p.Provider,
        TargetLanguage: string.IsNullOrEmpty(p.TargetLanguage) ? AiSettings.Default.TargetLanguage : p.TargetLanguage,
        UiCulture: string.IsNullOrEmpty(p.UiCulture) ? AiSettings.Default.UiCulture : p.UiCulture,
        Ollama:            FromPersisted(p.Ollama,           AiProviderType.Ollama),
        Anthropic:         FromPersisted(p.Anthropic,        AiProviderType.Anthropic),
        OpenAi:            FromPersisted(p.OpenAi,           AiProviderType.OpenAi),
        Gemini:            FromPersisted(p.Gemini,           AiProviderType.Gemini),
        Mistral:           FromPersisted(p.Mistral,          AiProviderType.Mistral),
        OpenAiCompatible:  FromPersisted(p.OpenAiCompatible, AiProviderType.OpenAiCompatible));

    private static PersistedConfig ToPersisted(AiProviderConfig c) =>
        new(c.Endpoint, c.Model, SecretProtection.Protect(c.ApiKey));

    private static AiProviderConfig FromPersisted(PersistedConfig? c, AiProviderType fallbackType)
    {
        if (c is null) return AiDefaults.Config(fallbackType);
        return new AiProviderConfig(
            Endpoint: string.IsNullOrEmpty(c.Endpoint) ? AiDefaults.Endpoint(fallbackType) : c.Endpoint,
            Model: string.IsNullOrEmpty(c.Model) ? AiDefaults.Model(fallbackType) : c.Model,
            ApiKey: SecretProtection.Unprotect(c.ApiKeyProtected));
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
