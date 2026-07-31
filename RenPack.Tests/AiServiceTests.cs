using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using RenPack.Services;
using Xunit;

namespace RenPack.Tests;

public sealed class AiSettingsServiceTests : IDisposable
{
    private readonly string _configPath = Path.Combine(Path.GetTempPath(),
        "renpack-ai-" + Guid.NewGuid().ToString("N"), "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_configPath)!, recursive: true); }
        catch { /* egal */ }
    }

    [Fact]
    public void Fresh_install_returns_defaults()
    {
        var svc = new AiSettingsService(_configPath);
        svc.Current.Provider.Should().Be(AiProviderType.None);
        svc.Current.Ollama.Endpoint.Should().Be("http://localhost:11434");
        svc.Current.Ollama.Model.Should().NotBeNullOrEmpty();
        svc.Current.TargetLanguage.Should().NotBeNullOrEmpty();
        svc.Current.Anthropic.Endpoint.Should().Contain("anthropic.com");
    }

    [Fact]
    public void Update_persists_settings_atomically_and_encrypts_apikey()
    {
        var svc1 = new AiSettingsService(_configPath);
        var settings = AiSettings.Default with
        {
            Provider = AiProviderType.Anthropic,
            TargetLanguage = "Französisch",
            Anthropic = new AiProviderConfig("https://api.anthropic.com/v1", "claude-sonnet-4-6", "sk-ant-super-secret-42"),
        };
        svc1.Update(settings);

        File.Exists(_configPath).Should().BeTrue();
        File.Exists(_configPath + ".tmp").Should().BeFalse("die Temp-Datei muss nach Move weg sein");

        var raw = File.ReadAllText(_configPath);
        raw.Should().NotContain("sk-ant-super-secret-42", "API-Keys dürfen nicht im Klartext gespeichert werden");

        var svc2 = new AiSettingsService(_configPath);
        svc2.Current.Provider.Should().Be(AiProviderType.Anthropic);
        svc2.Current.TargetLanguage.Should().Be("Französisch");
        svc2.Current.Anthropic.Model.Should().Be("claude-sonnet-4-6");
        svc2.Current.Anthropic.ApiKey.Should().Be("sk-ant-super-secret-42",
            "beim Laden wird der Key entschlüsselt zurückgegeben");
    }

    [Fact]
    public void Corrupted_config_falls_back_to_defaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        File.WriteAllText(_configPath, "{{ definitely not json");

        var svc = new AiSettingsService(_configPath);
        svc.Current.Provider.Should().Be(AiProviderType.None);
    }
}

public sealed class TranslationServiceTests
{
    [Fact]
    public async Task Cached_names_are_not_requested_again()
    {
        var provider = new CountingProvider();
        var svc = new TranslationService();
        svc.ResetCacheIfNeeded(provider.Name, "Deutsch");

        var first = await svc.TranslateAsync(provider, ["money", "hp"], "Deutsch", cancellationToken: TestContext.Current.CancellationToken);
        first.Should().ContainKey("money").And.ContainKey("hp");
        provider.Requested.Should().BeEquivalentTo("money", "hp");

        provider.Requested.Clear();
        var second = await svc.TranslateAsync(provider, ["money", "hp"], "Deutsch", cancellationToken: TestContext.Current.CancellationToken);
        second.Should().ContainKey("money").And.ContainKey("hp");
        provider.Requested.Should().BeEmpty("beide Namen sollten aus dem Cache kommen");
    }

    [Fact]
    public async Task Changing_language_invalidates_cache()
    {
        var provider = new CountingProvider();
        var svc = new TranslationService();

        svc.ResetCacheIfNeeded(provider.Name, "Deutsch");
        await svc.TranslateAsync(provider, ["money"], "Deutsch", cancellationToken: TestContext.Current.CancellationToken);
        provider.Requested.Should().Contain("money");

        provider.Requested.Clear();
        svc.ResetCacheIfNeeded(provider.Name, "Englisch");
        await svc.TranslateAsync(provider, ["money"], "Englisch", cancellationToken: TestContext.Current.CancellationToken);
        provider.Requested.Should().Contain("money", "nach Sprach-Wechsel muss neu übersetzt werden");
    }

    private sealed class CountingProvider : IAiProvider
    {
        public string Name => "Counting";
        public List<string> Requested { get; } = [];
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyDictionary<string, string>> TranslateBatchAsync(
            IReadOnlyList<string> names, string targetLanguage, CancellationToken ct = default)
        {
            Requested.AddRange(names);
            return Task.FromResult<IReadOnlyDictionary<string, string>>(
                names.ToDictionary(n => n, n => n + "-übersetzt"));
        }
    }
}

public sealed class OllamaProviderTests
{
    [Fact]
    public async Task Parses_translations_from_json_response()
    {
        var handler = new StubHandler((request, ct) =>
        {
            request.RequestUri!.PathAndQuery.Should().Be("/api/chat");
            var body = """
                {
                  "message": {
                    "role": "assistant",
                    "content": "{\"money\": \"Geld\", \"has_key\": \"Hat Schlüssel\"}"
                  }
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });
        var provider = new OllamaProvider(new HttpClient(handler), "http://x", "test-model");

        var result = await provider.TranslateBatchAsync(["money", "has_key"], "Deutsch", TestContext.Current.CancellationToken);
        result.Should().ContainKey("money").WhoseValue.Should().Be("Geld");
        result.Should().ContainKey("has_key").WhoseValue.Should().Be("Hat Schlüssel");
    }

    [Fact]
    public async Task Strips_markdown_code_fences_from_response()
    {
        var handler = new StubHandler((_, _) =>
        {
            var body = """
                {
                  "message": {
                    "role": "assistant",
                    "content": "```json\n{\"money\": \"Geld\"}\n```"
                  }
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });
        var provider = new OllamaProvider(new HttpClient(handler), "http://x", "m");

        var result = await provider.TranslateBatchAsync(["money"], "Deutsch", TestContext.Current.CancellationToken);
        result.Should().ContainKey("money").WhoseValue.Should().Be("Geld");
    }

    [Fact]
    public async Task Returns_empty_when_response_is_not_json()
    {
        var handler = new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"message\": {\"role\":\"assistant\",\"content\":\"kein json\"}}",
                    Encoding.UTF8, "application/json")
            });
        var provider = new OllamaProvider(new HttpClient(handler), "http://x", "m");
        var result = await provider.TranslateBatchAsync(["money"], "Deutsch", TestContext.Current.CancellationToken);
        result.Should().BeEmpty();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request, ct));
    }
}

public sealed class AnthropicProviderTests
{
    [Fact]
    public async Task Sends_correct_headers_and_parses_response()
    {
        HttpRequestMessage? seen = null;
        var handler = new StubHandler((req, _) =>
        {
            seen = req;
            var body = """
                {
                  "content": [
                    { "type": "text", "text": "{\"money\": \"Geld\", \"hp\": \"Lebenspunkte\"}" }
                  ]
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });
        var provider = new AnthropicProvider(new HttpClient(handler),
            "https://api.anthropic.com/v1", "claude-sonnet-4-6", "sk-ant-123");

        var result = await provider.TranslateBatchAsync(["money", "hp"], "Deutsch",
            TestContext.Current.CancellationToken);

        seen.Should().NotBeNull();
        seen!.Headers.GetValues("x-api-key").Should().ContainSingle().Which.Should().Be("sk-ant-123");
        seen.Headers.GetValues("anthropic-version").Should().ContainSingle().Which.Should().Be("2023-06-01");
        result["money"].Should().Be("Geld");
        result["hp"].Should().Be("Lebenspunkte");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request, ct));
    }
}

public sealed class OpenAiCompatibleProviderTests
{
    [Fact]
    public async Task Sends_bearer_and_uses_json_object_response_format()
    {
        HttpRequestMessage? seen = null;
        string? seenBody = null;
        var handler = new StubHandler((req, ct) =>
        {
            seen = req;
            seenBody = req.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            var body = """
                {
                  "choices": [
                    { "message": { "role": "assistant", "content": "{\"money\": \"Geld\"}" } }
                  ]
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler),
            "https://api.openai.com/v1", "gpt-4o-mini", "sk-key-99");

        var result = await provider.TranslateBatchAsync(["money"], "Deutsch",
            TestContext.Current.CancellationToken);

        seen!.Headers.Authorization!.Scheme.Should().Be("Bearer");
        seen.Headers.Authorization.Parameter.Should().Be("sk-key-99");
        seenBody.Should().Contain("\"response_format\"").And.Contain("\"json_object\"");
        result["money"].Should().Be("Geld");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request, ct));
    }
}

public sealed class GeminiProviderTests
{
    [Fact]
    public async Task Puts_api_key_into_query_string_and_sets_response_mime_type()
    {
        HttpRequestMessage? seen = null;
        string? seenBody = null;
        var handler = new StubHandler((req, ct) =>
        {
            seen = req;
            seenBody = req.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            var body = """
                {
                  "candidates": [
                    { "content": { "role": "model", "parts": [ { "text": "{\"money\": \"Geld\"}" } ] } }
                  ]
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });
        var provider = new GeminiProvider(new HttpClient(handler),
            "https://generativelanguage.googleapis.com/v1beta", "gemini-2.0-flash", "AIza-XYZ");

        var result = await provider.TranslateBatchAsync(["money"], "Deutsch",
            TestContext.Current.CancellationToken);

        seen!.RequestUri!.Query.Should().Contain("key=AIza-XYZ");
        seenBody.Should().Contain("response_mime_type").And.Contain("application/json");
        result["money"].Should().Be("Geld");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request, ct));
    }
}

public sealed class OllamaPullProgressParserTests
{
    [Fact]
    public void Parses_downloading_status_with_bytes()
    {
        var evt = TestHelpers.ParseLine("""{"status":"downloading","digest":"sha256:abc","total":1000,"completed":420}""");
        evt.Should().NotBeNull();
        evt!.Status.Should().Be("downloading");
        evt.Total.Should().Be(1000);
        evt.Completed.Should().Be(420);
        evt.IsError.Should().BeFalse();
    }

    [Fact]
    public void Parses_success_status_without_bytes()
    {
        var evt = TestHelpers.ParseLine("""{"status":"success"}""");
        evt.Should().NotBeNull();
        evt!.Status.Should().Be("success");
        evt.Total.Should().BeNull();
        evt.Completed.Should().BeNull();
    }

    [Fact]
    public void Parses_error_field_into_iserror_true()
    {
        var evt = TestHelpers.ParseLine("""{"error":"pull failed: manifest missing"}""");
        evt.Should().NotBeNull();
        evt!.IsError.Should().BeTrue();
        evt.ErrorMessage.Should().Contain("manifest missing");
    }

    [Fact]
    public void Silently_ignores_blank_and_invalid_lines()
    {
        TestHelpers.ParseLine("").Should().BeNull();
        TestHelpers.ParseLine("nicht mal ansatzweise json").Should().BeNull();
    }
}

public sealed class SecretProtectionTests
{
    [Fact]
    public void Roundtrip_returns_same_plaintext()
    {
        string plain = "sk-ant-super-secret-1234567890";
        var protectedText = SecretProtection.Protect(plain);
        protectedText.Should().NotBeNull().And.StartWith("v1:").And.NotContain(plain);
        SecretProtection.Unprotect(protectedText).Should().Be(plain);
    }

    [Fact]
    public void Null_and_empty_roundtrip_to_null()
    {
        SecretProtection.Protect(null).Should().BeNull();
        SecretProtection.Protect("").Should().BeNull();
        SecretProtection.Unprotect(null).Should().BeNull();
        SecretProtection.Unprotect("").Should().BeNull();
    }

    [Fact]
    public void Unprotect_returns_null_on_garbled_input()
    {
        SecretProtection.Unprotect("v1:not-valid-base64!!!").Should().BeNull();
        SecretProtection.Unprotect("kein-v1-prefix").Should().BeNull();
    }
}

internal static class TestHelpers
{
    /// <summary>Ruft den internen <c>OllamaPullProgressParser.ParseLine</c> per
    /// Reflection auf, damit die Tests nicht `internal` sichtbar machen müssen.</summary>
    public static OllamaPullEvent? ParseLine(string line)
    {
        var t = typeof(OllamaProvider).Assembly
            .GetType("RenPack.Services.OllamaPullProgressParser")!;
        var m = t.GetMethod("ParseLine", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (OllamaPullEvent?)m.Invoke(null, [line]);
    }
}
