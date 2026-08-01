using FluentAssertions;
using RenPack.Services.Modding;
using Xunit;

namespace RenPack.Tests;

public sealed class KrosteWalkthroughGeneratorTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(),
        $"renpack-wt-tests-{Guid.NewGuid():N}");
    private readonly RenpyModAnalyzer _analyzer = new();
    private readonly KrosteWalkthroughGenerator _gen = new();

    public KrosteWalkthroughGeneratorTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private (string src, string dst) SetupDirs()
    {
        var src = Path.Combine(_tmp, "src");
        var dst = Path.Combine(_tmp, "dst");
        Directory.CreateDirectory(src);
        return (src, dst);
    }

    [Fact]
    public void Formats_hint_from_numeric_and_bool_deltas()
    {
        var choice = new RpyChoice("script.rpy", 1, "start", 0, 0, 0, "Say hi",
            Condition: null,
            Deltas: new[]
            {
                new VarDelta("love", "+=", "3"),
                new VarDelta("respect", "-=", "1"),
                new VarDelta("day0s2_compliment", "=", "True"),
            });
        var hint = KrosteWalkthroughGenerator.FormatHint(choice);
        hint.Should().StartWith("{color=#e0b14c}");
        hint.Should().EndWith("{/color}");
        // Runde Klammern statt [[ — sonst crashen Spiele mit custom
        // screens.rpy, die den Choice-Text doppelt substituieren
        // (verifiziert an Sophia Parker 0.230, v0.8.3-Bug).
        hint.Should().Contain("(K love+3)");
        hint.Should().Contain("(K respect-1)");
        hint.Should().Contain("(K day0s2_compliment set)");
    }

    [Fact]
    public void Hint_contains_no_square_brackets_to_survive_double_substitution()
    {
        // Regression-Test fuer v0.8.3-Bug: [ und ] duerfen im Hint NICHT
        // vorkommen — sonst interpretiert Ren'Py's Text-Substitution sie
        // als Python-Interpolation. Runde Klammern sind immun.
        var choice = new RpyChoice("f.rpy", 1, "l", 0, 0, 0, "t", null,
            new[]
            {
                new VarDelta("filthy", "+=", "1"),
                new VarDelta("choice", "=", "True"),
            });
        var hint = KrosteWalkthroughGenerator.FormatHint(choice);
        hint.Should().NotContain("[");
        hint.Should().NotContain("]");
    }

    [Fact]
    public void Empty_deltas_produce_empty_hint()
    {
        var choice = new RpyChoice("f.rpy", 1, "l", 0, 0, 0, "t", null, Array.Empty<VarDelta>());
        KrosteWalkthroughGenerator.FormatHint(choice).Should().Be("");
    }

    [Fact]
    public void Patches_source_file_and_writes_to_destination()
    {
        var (src, dst) = SetupDirs();
        File.WriteAllText(Path.Combine(src, "script.rpy"), """
            label start:
                menu:
                    "Be nice":
                        $ love += 3
                    "Be rude":
                        $ love -= 2
            """);
        var analysis = _analyzer.Analyze(src);
        int written = _gen.Generate(src, dst, analysis);

        written.Should().Be(1);
        var patched = File.ReadAllText(Path.Combine(dst, "script.rpy"));

        // Beide Choices haben jetzt Hint-Suffix VOR dem schliessenden ".
        // Runde Klammern (nicht [ ]!) sind immun gegen Ren'Py-Substitution.
        patched.Should().Contain("\"Be nice {color=#e0b14c}(K love+3){/color}\":");
        patched.Should().Contain("\"Be rude {color=#e0b14c}(K love-2){/color}\":");

        // Restliche Struktur bleibt erhalten (Label, $-Statements).
        patched.Should().Contain("label start:");
        patched.Should().Contain("$ love += 3");
    }

    [Fact]
    public void Skips_files_without_choices()
    {
        var (src, dst) = SetupDirs();
        File.WriteAllText(Path.Combine(src, "chars.rpy"), """
            define Maria = Character("Maria", color="#FF00FF")
            default love = 0
            """);
        File.WriteAllText(Path.Combine(src, "with-choices.rpy"), """
            label start:
                menu:
                    "Only choice":
                        $ love += 1
            """);
        var analysis = _analyzer.Analyze(src);
        int written = _gen.Generate(src, dst, analysis);
        written.Should().Be(1); // nur with-choices.rpy
        File.Exists(Path.Combine(dst, "with-choices.rpy")).Should().BeTrue();
        File.Exists(Path.Combine(dst, "chars.rpy")).Should().BeFalse();
    }

    [Fact]
    public void Writes_readme_with_statistics()
    {
        var (src, dst) = SetupDirs();
        File.WriteAllText(Path.Combine(src, "script.rpy"), """
            default love = 0
            label start:
                menu:
                    "Choice A":
                        $ love += 1
                    "Choice B":
                        $ love -= 1
            """);
        var analysis = _analyzer.Analyze(src);
        _gen.Generate(src, dst, analysis);

        var readme = File.ReadAllText(Path.Combine(dst, "KROSTEMOD_README.md"));
        readme.Should().Contain("KrosteMod");
        readme.Should().Contain("Patched files: 1");
        readme.Should().Contain("Choices annotated: 2");
        readme.Should().Contain("Store variables discovered: 1");
    }

    [Fact]
    public void Preserves_choice_condition_and_indentation()
    {
        var (src, dst) = SetupDirs();
        File.WriteAllText(Path.Combine(src, "script.rpy"), """
            label start:
                menu:
                    "Rich choice" if money > 100:
                        $ love += 5
            """);
        var analysis = _analyzer.Analyze(src);
        _gen.Generate(src, dst, analysis);
        var patched = File.ReadAllLines(Path.Combine(dst, "script.rpy"));
        var choiceLine = patched.Single(l => l.Contains("Rich choice"));
        choiceLine.Should().StartWith("        \""); // Original-Einrueckung
        choiceLine.Should().Contain("if money > 100:");
        choiceLine.Should().Contain("(K love+5)");
    }
}
