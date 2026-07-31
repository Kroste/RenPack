using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;

namespace RenPack.Services;

/// <summary>
/// KI-Provider gegen einen lokalen Ollama-Server (Default:
/// http://localhost:11434). Nutzt <c>/api/chat</c> im JSON-Mode für
/// strukturierte Übersetzungs-Antworten und <c>/api/tags</c> für die
/// Modellliste.
///
/// Ollama muss lokal installiert sein und laufen (<c>ollama serve</c>). Das
/// Modell muss vorher gepulled sein (<c>ollama pull gemma3:1b</c>) — ein
/// integrierter Pull mit Fortschritt folgt in v0.4b.
/// </summary>
public sealed class OllamaProvider : IAiProvider
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _model;

    public OllamaProvider(HttpClient http, string endpoint, string model)
    {
        _http = http;
        _endpoint = endpoint.TrimEnd('/');
        _model = model;
    }

    public string Name => $"Ollama ({_model})";

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var res = await _http.GetAsync($"{_endpoint}/api/tags", cancellationToken);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Ollama-Verfügbarkeit-Check fehlgeschlagen: {ep}", _endpoint);
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var res = await _http.GetFromJsonAsync<TagsResponse>(
                $"{_endpoint}/api/tags", cancellationToken);
            return res?.Models?.Select(m => m.Name).ToList() ?? [];
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Ollama-Modellliste nicht abrufbar: {ep}", _endpoint);
            return [];
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> TranslateBatchAsync(
        IReadOnlyList<string> variableNames,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (variableNames.Count == 0) return new Dictionary<string, string>();

        // Ollama-Prompt: system + user, JSON-Mode für parsebares Ergebnis.
        var systemPrompt =
            $"Du übersetzt Ren'Py-Save-Variablennamen in {targetLanguage}. " +
            "Diese Namen kommen aus Adult Visual Novels und beschreiben " +
            "Spielzustand, Beziehungen, Events, Story-Flags. Antworte NUR mit " +
            "gültigem JSON in der Form { \"name\": \"deutscheBeschreibung\", … }. " +
            "Beschreibungen sind kurz (max 6 Wörter), sinnvoll und in " +
            $"{targetLanguage}. Wenn ein Name nicht eindeutig übersetzbar ist, " +
            "gib eine sinnvolle Vermutung.";

        var namesJson = JsonSerializer.Serialize(variableNames);
        var userPrompt =
            $"Übersetze diese {variableNames.Count} Variablennamen. " +
            $"Antworte NUR mit dem JSON-Objekt (ohne Markdown, ohne Erklärung): {namesJson}";

        var req = new ChatRequest(
            Model: _model,
            Messages: [new ChatMessage("system", systemPrompt), new ChatMessage("user", userPrompt)],
            Stream: false,
            Format: "json");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var response = await _http.PostAsJsonAsync($"{_endpoint}/api/chat", req, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Ollama-Antwort war leer.");
        Log.Debug("Ollama {model}: {names} Namen in {ms} ms",
            _model, variableNames.Count, sw.ElapsedMilliseconds);

        return ParseTranslations(body.Message?.Content ?? "", variableNames);
    }

    private static IReadOnlyDictionary<string, string> ParseTranslations(
        string jsonPayload, IReadOnlyList<string> requestedNames)
    {
        var result = new Dictionary<string, string>(requestedNames.Count, StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(jsonPayload)) return result;

        // Ollama liefert im JSON-Mode meist reines JSON; manche Modelle wrappen
        // trotzdem in Markdown-Codeblöcken — kurzer Cleanup.
        string cleaned = jsonPayload.Trim();
        if (cleaned.StartsWith("```"))
        {
            int firstNewline = cleaned.IndexOf('\n');
            if (firstNewline > 0) cleaned = cleaned[(firstNewline + 1)..];
            int lastFence = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence > 0) cleaned = cleaned[..lastFence];
            cleaned = cleaned.Trim();
        }

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
            Log.Warn(ex, "Ollama-Antwort war kein gültiges JSON: {snippet}",
                cleaned.Length > 200 ? cleaned[..200] + "…" : cleaned);
        }
        return result;
    }

    // ---- DTOs ---------------------------------------------------------------

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("format")] string Format);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatResponse(
        [property: JsonPropertyName("message")] ChatMessage? Message);

    private sealed record TagsResponse(
        [property: JsonPropertyName("models")] IReadOnlyList<TagModel>? Models);

    private sealed record TagModel(
        [property: JsonPropertyName("name")] string Name);
}
