using FluentAssertions;
using RenPack.Services;
using RenPack.Services.Modding;
using Xunit;

namespace RenPack.Tests;

/// <summary>Tests fuer den KI-Rewriter (E4b). Nutzt einen Fake-Provider —
/// keine echten HTTP-Calls, damit die Tests deterministisch und offline
/// laufen.</summary>
public sealed class KrosteAiRewriterTests
{
    [Fact]
    public async Task Filters_says_that_dont_mention_any_target_name()
    {
        var provider = new FakeProvider(_ => "{}");
        var rewriter = new KrosteAiRewriter(provider);
        var says = new[]
        {
            new RpySayStatement("f.rpy", 1, "sam", "Hallo Welt"),
            new RpySayStatement("f.rpy", 2, "sam", "Hi Sophia!"),
        };
        var mappings = new Dictionary<string, string> { ["Sophia"] = "Anna" };
        await rewriter.ProposeRewritesAsync(says, mappings, ct: TestContext.Current.CancellationToken);
        // Nur der 2. Say hat den Namen — nur dieser landet im User-Prompt
        provider.LastUserPrompt.Should().Contain("Hi Sophia!");
        provider.LastUserPrompt.Should().NotContain("Hallo Welt");
    }

    [Fact]
    public async Task Word_boundary_prevents_substring_matches()
    {
        // "Sam" darf nicht "Samsung" oder "Samurai" treffen.
        var provider = new FakeProvider(_ => "{}");
        var rewriter = new KrosteAiRewriter(provider);
        var says = new[]
        {
            new RpySayStatement("f.rpy", 1, "n", "The Samurai bought a Samsung."),
            new RpySayStatement("f.rpy", 2, "n", "Hi Sam!"),
        };
        var mappings = new Dictionary<string, string> { ["Sam"] = "Bob" };
        await rewriter.ProposeRewritesAsync(says, mappings, ct: TestContext.Current.CancellationToken);
        provider.LastUserPrompt.Should().Contain("Hi Sam!");
        provider.LastUserPrompt.Should().NotContain("Samsung");
    }

    [Fact]
    public async Task Parses_json_response_into_body_text_edits()
    {
        var provider = new FakeProvider(_ =>
            """{"0": "Hi Anna, how are you?"}""");
        var rewriter = new KrosteAiRewriter(provider);
        var says = new[]
        {
            new RpySayStatement("day22.rpy", 100, "sam", "Hi Sophia, how are you?"),
        };
        var mappings = new Dictionary<string, string> { ["Sophia"] = "Anna" };
        var edits = await rewriter.ProposeRewritesAsync(says, mappings, ct: TestContext.Current.CancellationToken);
        edits.Should().ContainSingle();
        edits[0].SourceFile.Should().Be("day22.rpy");
        edits[0].SourceLine.Should().Be(100);
        edits[0].OriginalText.Should().Be("Hi Sophia, how are you?");
        edits[0].NewText.Should().Be("Hi Anna, how are you?");
        edits[0].Accepted.Should().BeTrue("default: alle Vorschlaege akzeptiert");
    }

    [Fact]
    public async Task Skips_response_entries_that_dont_actually_change_text()
    {
        // Wenn die KI eine Zeile "umschreibt" die identisch zum Original
        // ist, ist das kein Edit — wir wollen den Preview-Dialog nicht mit
        // Noise-Zeilen fuellen.
        var provider = new FakeProvider(_ =>
            """{"0": "Hi Sophia!"}""");
        var rewriter = new KrosteAiRewriter(provider);
        var says = new[]
        {
            new RpySayStatement("f.rpy", 1, "n", "Hi Sophia!"),
        };
        var mappings = new Dictionary<string, string> { ["Sophia"] = "Anna" };
        var edits = await rewriter.ProposeRewritesAsync(says, mappings, ct: TestContext.Current.CancellationToken);
        edits.Should().BeEmpty();
    }

    [Fact]
    public async Task Handles_markdown_code_fences_around_json()
    {
        // Manche Modelle wrappen JSON in ```json ... ```-Fences. Unser Parser
        // muss das durchreichen.
        var provider = new FakeProvider(_ =>
            """
            Sure, here you go:
            ```json
            {"0": "Hi Anna!"}
            ```
            """);
        var rewriter = new KrosteAiRewriter(provider);
        var says = new[]
        {
            new RpySayStatement("f.rpy", 1, "n", "Hi Sophia!"),
        };
        var mappings = new Dictionary<string, string> { ["Sophia"] = "Anna" };
        var edits = await rewriter.ProposeRewritesAsync(says, mappings, ct: TestContext.Current.CancellationToken);
        edits.Should().ContainSingle().Which.NewText.Should().Be("Hi Anna!");
    }

    [Fact]
    public async Task Empty_mappings_returns_no_edits_without_provider_call()
    {
        int callCount = 0;
        var provider = new FakeProvider(_ => { callCount++; return "{}"; });
        var rewriter = new KrosteAiRewriter(provider);
        var says = new[] { new RpySayStatement("f.rpy", 1, "n", "Text") };
        var edits = await rewriter.ProposeRewritesAsync(says, new Dictionary<string, string>(), ct: TestContext.Current.CancellationToken);
        edits.Should().BeEmpty();
        callCount.Should().Be(0, "kein Provider-Call ohne effektive Mappings");
    }

    [Fact]
    public async Task Provider_exception_in_one_batch_does_not_kill_the_others()
    {
        // Wenn ein Batch fehlschlaegt (Netzwerk, Rate-Limit), sollen die
        // anderen weiterlaufen — Preview zeigt dann eben weniger Vorschlaege.
        int batchNum = 0;
        var provider = new FakeProvider(_ =>
        {
            batchNum++;
            if (batchNum == 1) throw new HttpRequestException("first batch fails");
            return """{"0": "rewrite ok"}""";
        });
        var rewriter = new KrosteAiRewriter(provider);
        // 30 Says mit Sophia → 2 Batches (BatchSize = 20)
        var says = Enumerable.Range(0, 30)
            .Select(i => new RpySayStatement("f.rpy", i + 1, "n", $"Sophia {i}"))
            .ToList();
        var mappings = new Dictionary<string, string> { ["Sophia"] = "Anna" };
        var edits = await rewriter.ProposeRewritesAsync(says, mappings, ct: TestContext.Current.CancellationToken);
        // Batch 1 (20 says) crashed → keine Edits. Batch 2 (10 says) succeeded
        // mit "0" → 1 Edit.
        edits.Should().ContainSingle();
    }

    // ---- E4c: Relations ----------------------------------------------------

    [Fact]
    public async Task Relation_only_mapping_triggers_rewrites_without_character_rename()
    {
        // Kein Character-Rename, nur Beziehungswoerter: der Rewriter muss
        // Says finden die relation-terms erwaehnen und den Provider aufrufen.
        var provider = new FakeProvider(_ =>
            """{"0": "I love my aunt very much."}""");
        var rewriter = new KrosteAiRewriter(provider);
        var says = new[]
        {
            new RpySayStatement("f.rpy", 1, "sam", "I love my mother very much."),
            new RpySayStatement("f.rpy", 2, "sam", "The weather is fine."),
        };
        var relations = new Dictionary<string, string> { ["mother"] = "aunt" };
        var edits = await rewriter.ProposeRewritesAsync(
            says,
            new Dictionary<string, string>(),  // keine Character-Mappings
            relations,
            ct: TestContext.Current.CancellationToken);
        edits.Should().ContainSingle();
        edits[0].OriginalText.Should().Be("I love my mother very much.");
        edits[0].NewText.Should().Be("I love my aunt very much.");
    }

    [Fact]
    public async Task Relation_and_character_mappings_combined_in_prompt()
    {
        var provider = new FakeProvider(_ => "{}");
        var rewriter = new KrosteAiRewriter(provider);
        var says = new[]
        {
            new RpySayStatement("f.rpy", 1, "n", "Hi Sophia, tell my mother."),
        };
        var chars = new Dictionary<string, string> { ["Sophia"] = "Anna" };
        var rels = new Dictionary<string, string> { ["mother"] = "aunt" };
        await rewriter.ProposeRewritesAsync(says, chars, rels,
            ct: TestContext.Current.CancellationToken);
        // Der System-Prompt muss BEIDE Sektionen enthalten
        var sys = KrosteAiRewriter.BuildSystemPrompt(chars, rels);
        sys.Should().Contain("Character name replacements");
        sys.Should().Contain("\"Sophia\" → \"Anna\"");
        sys.Should().Contain("Relationship/vocabulary replacements");
        sys.Should().Contain("\"mother\" → \"aunt\"");
    }

    [Fact]
    public async Task Relation_word_boundary_prevents_partial_matches()
    {
        // "mother" darf nicht "grandmother" oder "smothering" ohne
        // Word-Boundary matchen — der Regex wraps beide in \b.
        var provider = new FakeProvider(_ => "{}");
        var rewriter = new KrosteAiRewriter(provider);
        var says = new[]
        {
            new RpySayStatement("f.rpy", 1, "n", "The smothering heat."),
            new RpySayStatement("f.rpy", 2, "n", "Mother!"),
        };
        var rels = new Dictionary<string, string> { ["Mother"] = "Aunt" };
        await rewriter.ProposeRewritesAsync(says, new Dictionary<string, string>(), rels,
            ct: TestContext.Current.CancellationToken);
        provider.LastUserPrompt.Should().Contain("Mother!");
        provider.LastUserPrompt.Should().NotContain("smothering");
    }

    [Fact]
    public async Task No_terms_at_all_returns_empty_without_provider_call()
    {
        int callCount = 0;
        var provider = new FakeProvider(_ => { callCount++; return "{}"; });
        var rewriter = new KrosteAiRewriter(provider);
        var says = new[] { new RpySayStatement("f.rpy", 1, "n", "Text") };
        var edits = await rewriter.ProposeRewritesAsync(
            says,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            ct: TestContext.Current.CancellationToken);
        edits.Should().BeEmpty();
        callCount.Should().Be(0);
    }

    // ---- Fake ------------------------------------------------------------

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
