using System.Text.Json;
using NLog;

namespace RenPack.Services;

/// <summary>
/// Prompt-Vorlagen für die Übersetzung von Ren'Py-Save-Variablennamen sowie
/// ein toleranter Parser für die JSON-Antworten. Wird von allen Providern
/// (Ollama, OpenAI-kompatibel, Anthropic, Gemini) gleichermaßen genutzt —
/// so bleibt die Übersetzungsqualität zwischen den Anbietern vergleichbar
/// und der Cache-Key sinnvoll wiederverwendbar.
/// </summary>
internal static class PromptBuilder
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public static string System(string targetLanguage) =>
        $"Du übersetzt Ren'Py-Save-Variablennamen in {targetLanguage}. " +
        "Diese Namen kommen aus (oft adult-orientierten) Visual Novels und " +
        "beschreiben Spielzustand, Beziehungen, Events, Story-Flags. Antworte " +
        "NUR mit gültigem JSON in der Form { \"name\": \"beschreibung\", … }. " +
        $"Beschreibungen sind kurz (max 6 Wörter), sinnvoll und in {targetLanguage}. " +
        "Wenn ein Name nicht eindeutig übersetzbar ist, gib eine sinnvolle Vermutung " +
        "basierend auf den Wortbestandteilen.";

    public static string User(IReadOnlyList<string> variableNames)
    {
        var json = JsonSerializer.Serialize(variableNames);
        return $"Übersetze diese {variableNames.Count} Variablennamen. Antworte NUR " +
            $"mit dem JSON-Objekt (kein Markdown, keine Erklärung): {json}";
    }

    /// <summary>Parst die (idealerweise) JSON-Antwort in ein Dict. Entfernt
    /// Markdown-Code-Fences, wenn die Modelle sich nicht auf reines JSON
    /// einlassen. Nicht-parsebare Antworten liefern ein leeres Dict — der
    /// Aufrufer entscheidet, ob das ein Fehler ist.</summary>
    public static IReadOnlyDictionary<string, string> ParseTranslations(string jsonPayload)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(jsonPayload)) return result;

        string cleaned = jsonPayload.Trim();
        if (cleaned.StartsWith("```"))
        {
            int firstNewline = cleaned.IndexOf('\n');
            if (firstNewline > 0) cleaned = cleaned[(firstNewline + 1)..];
            int lastFence = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence > 0) cleaned = cleaned[..lastFence];
            cleaned = cleaned.Trim();
        }

        // Manche Modelle (v. a. Anthropic) wrappen das JSON in einen Einleitungssatz.
        // Wir extrahieren das erste {…}-Objekt, wenn Klammern gefunden werden.
        int braceStart = cleaned.IndexOf('{');
        int braceEnd = cleaned.LastIndexOf('}');
        if (braceStart >= 0 && braceEnd > braceStart)
            cleaned = cleaned[braceStart..(braceEnd + 1)];

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var v = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) result[prop.Name] = v.Trim();
                }
            }
        }
        catch (JsonException ex)
        {
            Log.Warn(ex, "KI-Antwort war kein gültiges JSON: {snippet}",
                cleaned.Length > 200 ? cleaned[..200] + "…" : cleaned);
        }
        return result;
    }
}
