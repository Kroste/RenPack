namespace RenPack.Services;

public enum AiProviderType
{
    /// <summary>KI-Übersetzung deaktiviert.</summary>
    None,
    /// <summary>Lokales Ollama (Default — datenschutzfreundlich, kein API-Key).</summary>
    Ollama,
    // Folgt in v0.4b:
    // Anthropic, OpenAI, Gemini
}

/// <summary>Persistente KI-Konfiguration der App. Landet als JSON im
/// User-AppData-Verzeichnis; Aufrufer holen sie über <see cref="AiSettingsService"/>.</summary>
public sealed record AiSettings(
    AiProviderType Provider,
    string OllamaEndpoint,
    string OllamaModel,
    string TargetLanguage)
{
    /// <summary>Empfohlene Defaults für frische Installationen.</summary>
    public static AiSettings Default => new(
        Provider: AiProviderType.None,
        OllamaEndpoint: "http://localhost:11434",
        OllamaModel: "gemma3:1b",
        TargetLanguage: DetectSystemLanguageName());

    private static string DetectSystemLanguageName()
    {
        // Nimmt die aktuelle UI-Culture und mappt sie auf einen Klartext-Namen,
        // den die KI im Prompt versteht ("Deutsch", "Englisch", …).
        var iso = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return iso switch
        {
            "de" => "Deutsch",
            "en" => "Englisch",
            "fr" => "Französisch",
            "es" => "Spanisch",
            "it" => "Italienisch",
            "ru" => "Russisch",
            "pt" => "Portugiesisch",
            "nl" => "Niederländisch",
            "pl" => "Polnisch",
            "ja" => "Japanisch",
            "zh" => "Chinesisch",
            _ => "Deutsch",
        };
    }
}
