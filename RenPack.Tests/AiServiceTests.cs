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
        svc.Current.OllamaEndpoint.Should().Be("http://localhost:11434");
        svc.Current.OllamaModel.Should().NotBeNullOrEmpty();
        svc.Current.TargetLanguage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Update_persists_settings_atomically()
    {
        var svc1 = new AiSettingsService(_configPath);
        svc1.Update(new AiSettings(AiProviderType.Ollama, "http://foo:1234", "llama3", "Französisch"));

        File.Exists(_configPath).Should().BeTrue();
        File.Exists(_configPath + ".tmp").Should().BeFalse("die Temp-Datei muss nach Move weg sein");

        var svc2 = new AiSettingsService(_configPath);
        svc2.Current.Provider.Should().Be(AiProviderType.Ollama);
        svc2.Current.OllamaEndpoint.Should().Be("http://foo:1234");
        svc2.Current.OllamaModel.Should().Be("llama3");
        svc2.Current.TargetLanguage.Should().Be("Französisch");
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
