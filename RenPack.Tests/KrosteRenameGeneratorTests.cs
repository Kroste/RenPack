using FluentAssertions;
using RenPack.Services.Modding;
using Xunit;

namespace RenPack.Tests;

/// <summary>Tests fuer den Character-Rename-Generator (KrosteMod E4).</summary>
public sealed class KrosteRenameGeneratorTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(),
        $"renpack-rename-tests-{Guid.NewGuid():N}");
    private readonly KrosteRenameGenerator _gen = new();

    public KrosteRenameGeneratorTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private static ModAnalysis MakeAnalysis(params RpyCharacter[] chars) => new(
        Choices: Array.Empty<RpyChoice>(),
        StoreVariables: Array.Empty<RpyStoreVariable>(),
        Characters: chars,
        AnalyzedFiles: Array.Empty<string>(),
        VariableConsumers: new Dictionary<string, IReadOnlyList<VarConsumer>>(),
        MenuLocations: Array.Empty<RpyMenuLocation>(),
        SayStatements: Array.Empty<RpySayStatement>());

    [Fact]
    public void Generates_rpy_that_mutates_character_name_attributes()
    {
        var analysis = MakeAnalysis(
            new RpyCharacter("Sophia", "Sophia", "#ff00ff"),
            new RpyCharacter("Sam", "Sam", null));
        var config = new RenameConfig(new Dictionary<string, string>
        {
            ["Sophia"] = "Anna",
            ["Sam"] = "Bob",
        });
        var path = _gen.Generate(_tmp, analysis, config);
        var content = File.ReadAllText(path);

        content.Should().Contain("init 1000 python:");
        content.Should().Contain("store.Sophia.name = \"Anna\"");
        content.Should().Contain("store.Sam.name = \"Bob\"");
        // Try/except-Guard damit ein einzelner Fail nicht das ganze Init killt
        content.Should().Contain("try:");
        content.Should().Contain("except Exception");
    }

    [Fact]
    public void Ignores_mappings_for_unknown_characters()
    {
        // Ghost-Namen (Character der nicht in analysis.Characters ist) darf
        // nicht in der Output-.rpy landen — wir wissen ja nicht dass es
        // wirklich einen store.Ghost gibt.
        var analysis = MakeAnalysis(new RpyCharacter("Sophia", "Sophia", null));
        var config = new RenameConfig(new Dictionary<string, string>
        {
            ["Ghost"] = "Something",
        });
        var path = _gen.Generate(_tmp, analysis, config);
        var content = File.ReadAllText(path);
        content.Should().NotContain("store.Ghost");
        content.Should().Contain("(keine effektiven Mappings");
    }

    [Fact]
    public void Ignores_empty_and_unchanged_new_names()
    {
        var analysis = MakeAnalysis(
            new RpyCharacter("Sophia", "Sophia", null),
            new RpyCharacter("Sam", "Sam", null),
            new RpyCharacter("Amber", "Amber", null));
        var config = new RenameConfig(new Dictionary<string, string>
        {
            ["Sophia"] = "",         // leer → skip
            ["Sam"] = "  ",          // nur whitespace → skip
            ["Amber"] = "Amber",     // gleicher Name → skip (kein tatsaechlicher Rename)
        });
        var path = _gen.Generate(_tmp, analysis, config);
        var content = File.ReadAllText(path);
        content.Should().NotContain("store.Sophia.name");
        content.Should().NotContain("store.Sam.name");
        content.Should().NotContain("store.Amber.name");
    }

    [Fact]
    public void Escapes_special_chars_in_new_names()
    {
        var analysis = MakeAnalysis(new RpyCharacter("Sophia", "Sophia", null));
        var config = new RenameConfig(new Dictionary<string, string>
        {
            ["Sophia"] = "Anna \"the Brave\"",
        });
        var path = _gen.Generate(_tmp, analysis, config);
        var content = File.ReadAllText(path);
        // Anfuehrungszeichen im Namen muessen escaped werden
        content.Should().Contain("\\\"the Brave\\\"");
    }

    // ---- v0.12.0 E4b: Body-Text-Patcher ----------------------------------

    [Fact]
    public void Applies_body_text_edits_to_source_rpy_files()
    {
        // Setup: dekompilierte .rpy im Source-Root, Config mit Body-Edits
        // → gepatchte .rpy im Dest-Dir.
        var srcRoot = Path.Combine(_tmp, "src");
        var dstRoot = Path.Combine(_tmp, "dst");
        Directory.CreateDirectory(srcRoot);
        File.WriteAllText(Path.Combine(srcRoot, "story.rpy"), """
            label start:
                sophia "Hi Sophia!"
                sam "How are you?"
            """);

        var analysis = MakeAnalysis(new RpyCharacter("Sophia", "Sophia", null));
        var config = new RenameConfig(
            Mappings: new Dictionary<string, string> { ["Sophia"] = "Anna" },
            BodyTextEdits:
            [
                new BodyTextEdit("story.rpy", 2, "Hi Sophia!", "Hi Anna!", Accepted: true),
            ]);
        _gen.Generate(dstRoot, analysis, config, decompiledSourceRoot: srcRoot);

        var patched = File.ReadAllText(Path.Combine(dstRoot, "story.rpy"));
        patched.Should().Contain("sophia \"Hi Anna!\"");
        patched.Should().NotContain("Hi Sophia!");
        // Andere Zeilen unangetastet
        patched.Should().Contain("sam \"How are you?\"");
    }

    [Fact]
    public void Skips_unaccepted_body_text_edits()
    {
        var srcRoot = Path.Combine(_tmp, "src2");
        var dstRoot = Path.Combine(_tmp, "dst2");
        Directory.CreateDirectory(srcRoot);
        File.WriteAllText(Path.Combine(srcRoot, "story.rpy"), """
            sophia "Hi Sophia!"
            """);

        var analysis = MakeAnalysis(new RpyCharacter("Sophia", "Sophia", null));
        var config = new RenameConfig(
            Mappings: new Dictionary<string, string> { ["Sophia"] = "Anna" },
            BodyTextEdits:
            [
                new BodyTextEdit("story.rpy", 1, "Hi Sophia!", "Hi Anna!", Accepted: false),
            ]);
        _gen.Generate(dstRoot, analysis, config, decompiledSourceRoot: srcRoot);

        // Ohne akzeptierte Edits soll die Datei GAR NICHT im dst landen —
        // sonst wuerde die unveraenderte Kopie via Deploy die Original-.rpyc
        // ohne Grund ueberschreiben.
        File.Exists(Path.Combine(dstRoot, "story.rpy")).Should().BeFalse();
    }

    [Fact]
    public void ReplaceInLine_preserves_character_prefix_and_indentation()
    {
        var line = "    sophia \"Hi Sophia!\"";
        var result = KrosteRenameGenerator.ReplaceInLine(line, "Hi Sophia!", "Hi Anna!");
        result.Should().Be("    sophia \"Hi Anna!\"");
    }

    [Fact]
    public void ReplaceInLine_returns_null_when_original_not_found()
    {
        var line = "sophia \"Different text\"";
        var result = KrosteRenameGenerator.ReplaceInLine(line, "Hi Sophia!", "Hi Anna!");
        result.Should().BeNull();
    }
}
