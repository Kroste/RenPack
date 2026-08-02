using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;

namespace RenPack.Services;

/// <summary>
/// KI-Provider gegen einen lokalen Ollama-Server (Default:
/// <c>http://localhost:11434</c>). Nutzt <c>/api/chat</c> mit <c>format="json"</c>
/// für strukturierte Übersetzungs-Antworten, <c>/api/tags</c> für die lokal
/// installierten Modelle und <c>/api/pull</c> (NDJSON-Streaming) zum
/// Herunterladen neuer Modelle direkt aus der App.
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
        _endpoint = NormalizeApiBase(endpoint);
        _model = model;
    }

    public string Name => $"Ollama ({_model})";

    /// <summary>Manche Anleitungen lassen die Nutzer <c>/v1</c> ans Ende hängen
    /// (OpenAI-kompatibles Interface). Für die nativen Ollama-Endpoints muss das
    /// weg — Allpaca macht das genauso.</summary>
    internal static string NormalizeApiBase(string endpoint)
    {
        var e = endpoint.TrimEnd('/');
        if (e.EndsWith("/v1", StringComparison.Ordinal)) e = e[..^3];
        return e;
    }

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
        IReadOnlyList<string> variableNames, string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (variableNames.Count == 0) return new Dictionary<string, string>();

        var content = await ChatAsync(
            PromptBuilder.System(targetLanguage),
            PromptBuilder.User(variableNames),
            jsonMode: true,
            cancellationToken);
        return PromptBuilder.ParseTranslations(content);
    }

    public Task<string> CompleteAsync(string systemPrompt, string userPrompt,
        CancellationToken cancellationToken = default) =>
        // jsonMode weglassen — Rewriter setzt System-Prompt so dass die
        // Antwort trotzdem JSON ist, aber Ollama zwingt sonst zu einem
        // Objekt-Layer der bei Freeform-Prompts stoert.
        ChatAsync(systemPrompt, userPrompt, jsonMode: false, cancellationToken);

    private async Task<string> ChatAsync(string systemPrompt, string userPrompt,
        bool jsonMode, CancellationToken cancellationToken)
    {
        var req = new ChatRequest(
            Model: _model,
            Messages: [
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", userPrompt),
            ],
            Stream: false,
            Format: jsonMode ? "json" : null);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var response = await _http.PostAsJsonAsync($"{_endpoint}/api/chat", req, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Ollama-Antwort war leer.");
        Log.Debug("Ollama {model}: chat in {ms} ms (json={json})",
            _model, sw.ElapsedMilliseconds, jsonMode);
        return body.Message?.Content ?? "";
    }

    /// <summary>Streamt die NDJSON-Events von <c>POST /api/pull</c>. Ein Event
    /// pro Status-Wechsel (pulling manifest → downloading mit Byte-Fortschritt →
    /// verifying → success). Cancel schließt die Verbindung sauber; der
    /// Ollama-Server bricht den Server-seitigen Pull daraufhin ab.</summary>
    public async IAsyncEnumerable<OllamaPullEvent> PullAsync(string modelName,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var url = $"{_endpoint}/api/pull";
        var body = JsonSerializer.Serialize(new { name = modelName });
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(req,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            yield return new OllamaPullEvent("error", null, null, null,
                IsError: true, ErrorMessage: $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}");
            yield break;
        }

        using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            var evt = OllamaPullProgressParser.ParseLine(line);
            if (evt is not null) yield return evt;
        }
    }

    // ---- DTOs ---------------------------------------------------------------

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("format"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Format);

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
