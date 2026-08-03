using FluentAssertions;
using RenPack.Services;
using RenPack.Services.Modding;
using Xunit;

namespace RenPack.Tests;

/// <summary>Tests fuer den KI-Batch-Uebersetzer (E6). Fake-Provider — keine
/// echten HTTP-Calls.</summary>
public sealed class KrosteAiTranslatorTests
{
    [Fact]
    public async Task Deduplicates_input_strings_before_provider_call()
    {
        var provider = new FakeProvider(_ => "{}");
        var translator = new KrosteAiTranslator(provider);
        var input = new[] { "Hello", "Hello", "World", "Hello", "World" };
        await translator.TranslateAsync(input, TargetLanguage.German,
            ct: TestContext.Current.CancellationToken);
        // 2 unique strings → beide im User-Prompt, jedes nur einmal
        provider.LastUserPrompt.Should().Contain("\"Hello\"");
        provider.LastUserPrompt.Should().Contain("\"World\"");
        // Nur EIN "Hello"-Vorkommen im Prompt (Dedup)
        var helloCount = System.Text.RegularExpressions.Regex.Matches(
            provider.LastUserPrompt, "\"Hello\"").Count;
        helloCount.Should().Be(1);
    }

    [Fact]
    public async Task Parses_translations_from_json_response()
    {
        var provider = new FakeProvider(_ =>
            """{"0": "Hallo", "1": "Welt"}""");
        var translator = new KrosteAiTranslator(provider);
        var input = new[] { "Hello", "World" };
        var result = await translator.TranslateAsync(input, TargetLanguage.German,
            ct: TestContext.Current.CancellationToken);
        result["Hello"].Should().Be("Hallo");
        result["World"].Should().Be("Welt");
    }

    [Fact]
    public async Task Skips_translations_identical_to_input()
    {
        // Wenn die KI eine Zeile "uebersetzt" die identisch zum Original ist
        // (kann bei kurzen Woertern wie "OK" passieren), soll sie nicht als
        // Uebersetzung angesetzt werden — sonst blaehen wir das tl-File unnoetig
        // auf mit `old "OK" \n new "OK"`.
        var provider = new FakeProvider(_ =>
            """{"0": "OK", "1": "Welt"}""");
        var translator = new KrosteAiTranslator(provider);
        var input = new[] { "OK", "World" };
        var result = await translator.TranslateAsync(input, TargetLanguage.German,
            ct: TestContext.Current.CancellationToken);
        result.Should().NotContainKey("OK");
        result["World"].Should().Be("Welt");
    }

    [Fact]
    public async Task Empty_input_returns_empty_without_provider_call()
    {
        int callCount = 0;
        var provider = new FakeProvider(_ => { callCount++; return "{}"; });
        var translator = new KrosteAiTranslator(provider);
        var result = await translator.TranslateAsync(
            Array.Empty<string>(), TargetLanguage.German,
            ct: TestContext.Current.CancellationToken);
        result.Should().BeEmpty();
        callCount.Should().Be(0);
    }

    [Fact]
    public void System_prompt_contains_target_language_name()
    {
        var prompt = KrosteAiTranslator.BuildSystemPrompt(TargetLanguage.German, null);
        prompt.Should().Contain("into German");
        // Ohne source-language darf kein "from <lang>" im Prompt sein
        prompt.Should().NotContain("from German");
        prompt.Should().NotContain("from English");
    }

    [Fact]
    public void System_prompt_with_source_language_mentions_both()
    {
        var prompt = KrosteAiTranslator.BuildSystemPrompt(
            TargetLanguage.Russian, TargetLanguage.English);
        prompt.Should().Contain("English");
        prompt.Should().Contain("Russian");
        prompt.Should().Contain("from English");
    }

    [Fact]
    public void Collects_translatable_strings_from_says_and_choices()
    {
        var say = new RpySayStatement("f.rpy", 1, "c", "Hello");
        var choice = new RpyChoice(
            SourceFile: "f.rpy", SourceLine: 5, Label: "start",
            MenuIndex: 0, MenuHeaderLine: 4, ChoiceIndex: 0,
            Text: "Yes", Condition: null, Deltas: []);
        var analysis = new ModAnalysis(
            [choice], [], [], [], new Dictionary<string, IReadOnlyList<VarConsumer>>(),
            [], [say]);
        var strings = KrosteAiTranslator.CollectTranslatableStrings(analysis);
        strings.Should().Contain("Hello");
        strings.Should().Contain("Yes");
    }

    private sealed class FakeProvider : IAiProvider
    {
        private readonly Func<string, string> _respond;
        public FakeProvider(Func<string, string> respond) => _respond = respond;
        public string Name => "Fake";
        public string LastUserPrompt { get; private set; } = "";
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyDictionary<string, string>> TranslateBatchAsync(
            IReadOnlyList<string> names, string targetLanguage, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt,
            CancellationToken ct = default)
        {
            LastUserPrompt = userPrompt;
            return Task.FromResult(_respond(userPrompt));
        }
    }
}
