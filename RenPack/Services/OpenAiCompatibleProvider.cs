using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;

namespace RenPack.Services;

/// <summary>
/// KI-Provider gegen OpenAI-kompatible Endpoints (POST /chat/completions). Deckt
/// OpenAI/ChatGPT, Mistral und beliebige andere Anbieter mit derselben API ab
/// (Groq, OpenRouter, LM Studio, …). Nutzt <c>response_format: {"type":
/// "json_object"}</c>, sodass die Modelle direkt sauberes JSON liefern.
/// </summary>
public sealed class OpenAiCompatibleProvider : IAiProvider
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string? _apiKey;
    private readonly string _displayName;

    public OpenAiCompatibleProvider(HttpClient http, string endpoint, string model, string? apiKey,
        string displayName = "OpenAI-kompatibel")
    {
        _http = http;
        _endpoint = endpoint.TrimEnd('/');
        _model = model;
        _apiKey = apiKey;
        _displayName = displayName;
    }

    public string Name => $"{_displayName} ({_model})";

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_endpoint}/models");
            AddAuth(req);
            using var res = await _http.SendAsync(req, cancellationToken);
            // Manche Anbieter verlangen POST /chat/completions, GET /models liefert 404 —
            // 401 ist ein starkes "erreichbar aber Auth-Problem"-Signal, das wir akzeptieren.
            return res.IsSuccessStatusCode || res.StatusCode == System.Net.HttpStatusCode.Unauthorized;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "{provider}-Verfügbarkeit-Check fehlgeschlagen: {ep}", _displayName, _endpoint);
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_endpoint}/models");
            AddAuth(req);
            using var res = await _http.SendAsync(req, cancellationToken);
            if (!res.IsSuccessStatusCode) return [];
            var body = await res.Content.ReadFromJsonAsync<ModelsResponse>(cancellationToken);
            return body?.Data?.Select(m => m.Id).ToList() ?? [];
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "{provider}-Modellliste nicht abrufbar: {ep}", _displayName, _endpoint);
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
            jsonObject: true,
            cancellationToken);
        return PromptBuilder.ParseTranslations(content);
    }

    public Task<string> CompleteAsync(string systemPrompt, string userPrompt,
        CancellationToken cancellationToken = default) =>
        ChatAsync(systemPrompt, userPrompt, jsonObject: false, cancellationToken);

    private async Task<string> ChatAsync(string systemPrompt, string userPrompt,
        bool jsonObject, CancellationToken cancellationToken)
    {
        var req = new ChatRequest(
            Model: _model,
            Messages: [new ChatMessage("system", systemPrompt), new ChatMessage("user", userPrompt)],
            ResponseFormat: jsonObject ? new ResponseFormat("json_object") : null,
            Temperature: 0.2);

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/chat/completions")
        {
            Content = JsonContent.Create(req),
        };
        AddAuth(httpReq);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var response = await _http.SendAsync(httpReq, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken)
            ?? throw new InvalidOperationException($"{_displayName}-Antwort war leer.");
        Log.Debug("{provider} {model}: chat in {ms} ms (json={json})",
            _displayName, _model, sw.ElapsedMilliseconds, jsonObject);
        return body.Choices?.FirstOrDefault()?.Message?.Content ?? "";
    }

    private void AddAuth(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(_apiKey))
            req.Headers.Add("Authorization", "Bearer " + _apiKey);
    }

    // ---- DTOs ---------------------------------------------------------------

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("response_format"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ResponseFormat? ResponseFormat,
        [property: JsonPropertyName("temperature")] double Temperature);

    private sealed record ResponseFormat(
        [property: JsonPropertyName("type")] string Type);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<Choice>? Choices);

    private sealed record Choice(
        [property: JsonPropertyName("message")] ChatMessage? Message);

    private sealed record ModelsResponse(
        [property: JsonPropertyName("data")] IReadOnlyList<ModelId>? Data);

    private sealed record ModelId(
        [property: JsonPropertyName("id")] string Id);
}
