namespace RenPack.Services;

/// <summary>Provider-Abstraktion für KI-Übersetzungen von Variablennamen.
/// v0.4a hat nur eine Implementierung (Ollama); Anthropic/OpenAI/Gemini folgen
/// in v0.4b nach Allpaca-Vorbild.</summary>
public interface IAiProvider
{
    /// <summary>Kurzer Anzeige-Name des Providers (für Logs und UI).</summary>
    string Name { get; }

    /// <summary>Prüft ohne Übersetzungs-Anfrage, ob der Provider ansprechbar ist
    /// (z. B. läuft ein Ollama-Server auf dem Endpoint?).</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>Listet die verfügbaren Modelle (Ollama: /api/tags; für andere
    /// Provider vorerst eine kuratierte Liste).</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Übersetzt eine Charge Variablennamen in die Zielsprache. Die
    /// Rückgabe ist ein Dict <c>{originalName → menschenlesbare Beschreibung}</c>.
    /// Für Namen, für die die KI keine sinnvolle Antwort liefert, fehlt der Key.</summary>
    Task<IReadOnlyDictionary<string, string>> TranslateBatchAsync(
        IReadOnlyList<string> variableNames,
        string targetLanguage,
        CancellationToken cancellationToken = default);
}
