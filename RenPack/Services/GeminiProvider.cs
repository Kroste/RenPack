using System.Net.Http.Json;
using System.Text.Json.Serialization;
using NLog;

namespace RenPack.Services;

/// <summary>
/// Google Gemini über die Generative-Language-API
/// (POST /v1beta/models/{model}:generateContent?key={apiKey}). Nutzt
/// <c>generation_config.response_mime_type = "application/json"</c>, damit
/// die Antwort direkt reines JSON ist.
/// </summary>
public sealed class GeminiProvider : IAiProvider
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string? _apiKey;

    public GeminiProvider(HttpClient http, string endpoint, string model, string? apiKey)
    {
        _http = http;
        _endpoint = endpoint.TrimEnd('/');
        _model = model;
        _apiKey = apiKey;
    }

    public string Name => $"Gemini ({_model})";

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(!string.IsNullOrWhiteSpace(_apiKey));

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(
        [
            "gemini-2.0-flash",
            "gemini-2.0-flash-lite",
            "gemini-2.5-pro",
            "gemini-2.5-flash",
        ]);

    public async Task<IReadOnlyDictionary<string, string>> TranslateBatchAsync(
        IReadOnlyList<string> variableNames, string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (variableNames.Count == 0) return new Dictionary<string, string>();
        var text = await ChatAsync(
            PromptBuilder.System(targetLanguage),
            PromptBuilder.User(variableNames),
            responseMimeType: "application/json",
            cancellationToken);
        return PromptBuilder.ParseTranslations(text);
    }

    public Task<string> CompleteAsync(string systemPrompt, string userPrompt,
        CancellationToken cancellationToken = default) =>
        ChatAsync(systemPrompt, userPrompt, responseMimeType: null, cancellationToken);

    private async Task<string> ChatAsync(string systemPrompt, string userPrompt,
        string? responseMimeType, CancellationToken cancellationToken)
    {
        var payload = new GeminiRequest(
            SystemInstruction: new SystemInstruction([new Part(systemPrompt)]),
            Contents: [new Content("user", [new Part(userPrompt)])],
            GenerationConfig: new GenerationConfig(ResponseMimeType: responseMimeType, Temperature: 0.2));

        var url = $"{_endpoint}/models/{_model}:generateContent?key={_apiKey}";
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload),
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var response = await _http.SendAsync(httpReq, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Gemini HTTP {(int)response.StatusCode}: {err}");
        }
        var body = await response.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Gemini-Antwort war leer.");
        Log.Debug("Gemini {model}: chat in {ms} ms (mime={mime})",
            _model, sw.ElapsedMilliseconds, responseMimeType);
        return body.Candidates?.FirstOrDefault()?.Content?.Parts?
            .Select(p => p.Text).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "";
    }

    // ---- DTOs ---------------------------------------------------------------

    private sealed record GeminiRequest(
        [property: JsonPropertyName("system_instruction")] SystemInstruction SystemInstruction,
        [property: JsonPropertyName("contents")] IReadOnlyList<Content> Contents,
        [property: JsonPropertyName("generationConfig")] GenerationConfig GenerationConfig);

    private sealed record SystemInstruction(
        [property: JsonPropertyName("parts")] IReadOnlyList<Part> Parts);

    private sealed record Content(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("parts")] IReadOnlyList<Part> Parts);

    private sealed record Part(
        [property: JsonPropertyName("text")] string Text);

    private sealed record GenerationConfig(
        [property: JsonPropertyName("response_mime_type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResponseMimeType,
        [property: JsonPropertyName("temperature")] double Temperature);

    private sealed record GeminiResponse(
        [property: JsonPropertyName("candidates")] IReadOnlyList<Candidate>? Candidates);

    private sealed record Candidate(
        [property: JsonPropertyName("content")] Content? Content);
}
