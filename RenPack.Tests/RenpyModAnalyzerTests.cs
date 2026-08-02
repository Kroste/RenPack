using FluentAssertions;
using RenPack.Services.Modding;
using Xunit;

namespace RenPack.Tests;

/// <summary>Tests fuer den Line-basierten .rpy-Parser des Mod-Analyzers.
/// Wir arbeiten mit inline .rpy-Snippets ueber temporaere Dateien —
/// realistischer als synthetische ClassDicts, weil der Analyzer bewusst
/// auf .rpy-Ebene lebt (das ist der Modder-Workflow: erst dekompilieren,
/// dann analysieren).</summary>
public sealed class RenpyModAnalyzerTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(),
        $"renpack-mod-tests-{Guid.NewGuid():N}");
    private readonly RenpyModAnalyzer _analyzer = new();

    public RenpyModAnalyzerTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private string Write(string relPath, string content)
    {
        var full = Path.Combine(_tmp, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Fact]
    public void Extracts_choices_with_deltas_from_menu()
    {
        Write("game/script.rpy", """
            label start:
                menu:
                    "Be nice":
                        $ love += 3
                        $ respect += 1
                    "Be rude":
                        $ love -= 2
                        $ respect -= 1
            """);
        var result = _analyzer.Analyze(_tmp);
        result.Choices.Should().HaveCount(2);
        var nice = result.Choices[0];
        nice.Text.Should().Be("Be nice");
        nice.Label.Should().Be("start");
        nice.MenuIndex.Should().Be(0);
        nice.ChoiceIndex.Should().Be(0);
        nice.Deltas.Should().HaveCount(2);
        nice.Deltas[0].Should().Be(new VarDelta("love", "+=", "3"));
        nice.Deltas[1].Should().Be(new VarDelta("respect", "+=", "1"));

        var rude = result.Choices[1];
        rude.Deltas[0].Should().Be(new VarDelta("love", "-=", "2"));
    }

    [Fact]
    public void Nested_menus_do_not_leak_deltas_into_parent_choice()
    {
        // Regressions-Test fuer den ersten Analyzer-Bug: verschachtelte
        // menu-Choices zogen ihre Deltas in den Parent-Choice mit rein.
        Write("game/script.rpy", """
            label start:
                menu:
                    "Outer choice":
                        $ love += 1
                        menu:
                            "Inner A":
                                $ respect += 100
                            "Inner B":
                                $ respect -= 100
                        $ sos += 2
            """);
        var result = _analyzer.Analyze(_tmp);
        result.Choices.Should().HaveCount(3); // outer + 2 inner

        var outer = result.Choices.Single(c => c.Text == "Outer choice");
        // Nur die direkten Deltas des outer choice, NICHT die aus dem
        // inner-menu. Das $ sos += 2 nach dem inner-menu gehoert wieder
        // zum outer-body — muss also drin sein.
        outer.Deltas.Should().HaveCount(2);
        outer.Deltas[0].Should().Be(new VarDelta("love", "+=", "1"));
        outer.Deltas[1].Should().Be(new VarDelta("sos", "+=", "2"));

        var innerA = result.Choices.Single(c => c.Text == "Inner A");
        innerA.Deltas.Should().ContainSingle();
        innerA.Deltas[0].Should().Be(new VarDelta("respect", "+=", "100"));
    }

    [Fact]
    public void Recognises_multiple_menus_in_same_label()
    {
        Write("game/script.rpy", """
            label chapter1:
                menu:
                    "First":
                        pass
                    "Second":
                        pass
                "some narration"
                menu:
                    "Third":
                        pass
            """);
        var result = _analyzer.Analyze(_tmp);
        result.Choices.Should().HaveCount(3);
        result.Choices[0].MenuIndex.Should().Be(0);
        result.Choices[1].MenuIndex.Should().Be(0);
        result.Choices[2].MenuIndex.Should().Be(1);
        result.Choices[2].ChoiceIndex.Should().Be(0);
    }

    [Fact]
    public void Extracts_default_variables_with_type_inference()
    {
        Write("game/vars.rpy", """
            default love = 0
            default happy = True
            default player_name = "Vince"
            default fraction = 0.5
            default items = []
            """);
        var result = _analyzer.Analyze(_tmp);
        result.StoreVariables.Should().HaveCount(5);
        result.StoreVariables.Single(v => v.Name == "love").TypeInferred.Should().Be("int");
        result.StoreVariables.Single(v => v.Name == "happy").TypeInferred.Should().Be("bool");
        result.StoreVariables.Single(v => v.Name == "player_name").TypeInferred.Should().Be("str");
        result.StoreVariables.Single(v => v.Name == "fraction").TypeInferred.Should().Be("float");
        result.StoreVariables.Single(v => v.Name == "items").TypeInferred.Should().Be("list");
    }

    [Fact]
    public void Extracts_characters_with_color()
    {
        Write("game/chars.rpy", """
            define Maria = Character("Maria", color="#FF00FF")
            define Vince = Character("[vince_name]", color="#98FB98")
            define Narrator = Character(None, kind=nvl)
            """);
        var result = _analyzer.Analyze(_tmp);
        result.Characters.Should().HaveCount(2); // Narrator hat kein Text-Name
        result.Characters.Single(c => c.VarName == "Maria").Color.Should().Be("#FF00FF");
        result.Characters.Single(c => c.VarName == "Vince").DisplayName.Should().Be("[vince_name]");
    }

    [Fact]
    public void Strips_trailing_comments_from_default_values()
    {
        // Regressions-Test: `default x = False  # comment` bekam frueher
        // "False        # comment" als TypeInferred=expr.
        Write("game/vars.rpy", """
            default splash_verified = False        # legal age confirmation
            """);
        var result = _analyzer.Analyze(_tmp);
        result.StoreVariables.Should().ContainSingle();
        var v = result.StoreVariables[0];
        v.DefaultValue.Should().Be("False");
        v.TypeInferred.Should().Be("bool");
    }

    [Fact]
    public void Ignores_translation_folder()
    {
        Write("game/script.rpy", """
            label start:
                menu:
                    "Original":
                        pass
            """);
        Write("game/tl/french/script.rpy", """
            label start:
                menu:
                    "Original":
                        pass
            """);
        var result = _analyzer.Analyze(_tmp);
        result.Choices.Should().ContainSingle(); // nur das Original, nicht die Uebersetzung
        result.AnalyzedFiles.Should().ContainSingle().And.Contain("game/script.rpy");
    }

    [Fact]
    public void Extracts_choice_condition()
    {
        Write("game/script.rpy", """
            label start:
                menu:
                    "Only visible when rich" if money > 100:
                        $ love += 1
                    "Always":
                        pass
            """);
        var result = _analyzer.Analyze(_tmp);
        result.Choices.Should().HaveCount(2);
        result.Choices[0].Condition.Should().Be("money > 100");
        result.Choices[1].Condition.Should().BeNull();
    }

    // ---- v0.9.0: Variable-Consumer-Erfassung ------------------------------

    [Fact]
    public void Collects_if_condition_as_variable_consumer()
    {
        Write("story.rpy", """
            label after_choice:
                if love >= 5:
                    "She smiles."
                elif love >= 2:
                    "She looks neutral."
                else:
                    "She frowns."
            """);
        var result = _analyzer.Analyze(_tmp);
        result.VariableConsumers.Should().ContainKey("love");
        var loveUsers = result.VariableConsumers["love"];
        loveUsers.Should().HaveCount(2, "if love + elif love — else zaehlt nicht");
        loveUsers.Should().OnlyContain(c => c.Kind == VarConsumerKind.Condition);
        loveUsers.Should().OnlyContain(c => c.Label == "after_choice");
    }

    [Fact]
    public void Collects_choice_condition_as_menu_gate_consumer()
    {
        Write("story.rpy", """
            label start:
                menu:
                    "Rich choice" if money > 100:
                        $ pass
                    "Normal choice":
                        $ pass
            """);
        var result = _analyzer.Analyze(_tmp);
        result.VariableConsumers.Should().ContainKey("money");
        result.VariableConsumers["money"].Should()
            .ContainSingle(c => c.Kind == VarConsumerKind.MenuChoiceGate);
    }

    [Fact]
    public void Filters_python_keywords_and_builtins_from_consumers()
    {
        Write("story.rpy", """
            label x:
                if love and not respect:
                    pass
            """);
        var result = _analyzer.Analyze(_tmp);
        result.VariableConsumers.Should().ContainKey("love");
        result.VariableConsumers.Should().ContainKey("respect");
        result.VariableConsumers.Should().NotContainKey("and");
        result.VariableConsumers.Should().NotContainKey("not");
    }

    // ---- v0.9.3: Menu-Header-Location + Choice-Impact ---------------------

    [Fact]
    public void Menu_locations_track_header_line_and_affected_vars()
    {
        Write("day22.rpy", """
            label day22_scene:
                menu:
                    "Be nice":
                        $ love += 3
                    "Be rude":
                        $ love -= 2
                        $ anger = True
            """);
        var result = _analyzer.Analyze(_tmp);
        result.MenuLocations.Should().HaveCount(1);
        var m = result.MenuLocations[0];
        m.SourceFile.Should().Be("day22.rpy");
        m.MenuHeaderLine.Should().Be(2, "Zeile 2 = 'menu:'");
        m.VariablesAffected.Should().BeEquivalentTo(new[] { "love", "anger" });
    }

    [Fact]
    public void Choice_records_its_menu_header_line_for_runtime_match()
    {
        Write("day22.rpy", """
            label x:
                "prelude"
                menu:
                    "A":
                        $ love += 1
            """);
        var result = _analyzer.Analyze(_tmp);
        result.Choices.Should().HaveCount(1);
        // Zeile 1 = label, Zeile 2 = say, Zeile 3 = menu:
        result.Choices[0].MenuHeaderLine.Should().Be(3);
    }

    // ---- v0.12.0: Say-Statement-Erfassung fuer E4b-Body-Rewrite ---------

    [Fact]
    public void Collects_say_statements_with_character_and_narrator_text()
    {
        Write("story.rpy", """
            label start:
                sophia "Hallo Welt"
                "Ein Beobachter spricht."
                sam "Wie geht's?"
            """);
        var result = _analyzer.Analyze(_tmp);
        result.SayStatements.Should().HaveCount(3);
        result.SayStatements.Should().Contain(s =>
            s.CharacterVar == "sophia" && s.RawTextInFile == "Hallo Welt");
        result.SayStatements.Should().Contain(s =>
            s.CharacterVar == "" && s.RawTextInFile == "Ein Beobachter spricht.");
        result.SayStatements.Should().Contain(s =>
            s.CharacterVar == "sam" && s.RawTextInFile == "Wie geht's?");
    }

    [Fact]
    public void Say_extraction_skips_lines_inside_menu_choices()
    {
        // Choice-Header "text": ist KEIN Say-Statement — wenn wir das faelsch-
        // licherweise als Say erfassen, wuerde der Rewriter Choice-Text als
        // Dialog-Text umschreiben und den Walkthrough zerreissen.
        Write("story.rpy", """
            label start:
                sam "Vor dem Menu"
                menu:
                    "Choice A":
                        sam "In choice a"
                    "Choice B":
                        pass
                sam "Nach dem Menu"
            """);
        var result = _analyzer.Analyze(_tmp);
        // Nur die Says AUSSERHALB des Menu-Scopes werden erfasst — das ist ok
        // fuer den Rewrite-Use-Case (Choice-Body-Says werden nicht umgeschrieben).
        result.SayStatements.Should().Contain(s => s.RawTextInFile == "Vor dem Menu");
        result.SayStatements.Should().Contain(s => s.RawTextInFile == "Nach dem Menu");
        result.SayStatements.Should().NotContain(s => s.RawTextInFile.StartsWith("Choice "));
    }

    [Fact]
    public void Say_extraction_preserves_escape_sequences()
    {
        // Der Text bleibt WIE ER IN DER RPY STEHT — inkl. \"-Escapes.
        // Sonst wuerde der Patcher spaeter nicht die exakte Zeile finden.
        Write("story.rpy", """
            label start:
                sam "Er sagt \"Hallo\""
            """);
        var result = _analyzer.Analyze(_tmp);
        result.SayStatements.Should().ContainSingle();
        result.SayStatements[0].RawTextInFile.Should().Be("Er sagt \\\"Hallo\\\"");
    }

    [Fact]
    public void Menus_without_var_changing_choices_are_excluded_from_locations()
    {
        // Ein reines Dialog-Menu (keine $-Statements) ist fuer den Hint
        // uninteressant — der Player sieht keinen Impact.
        Write("day22.rpy", """
            label x:
                menu:
                    "A":
                        "you said A"
                    "B":
                        "you said B"
            """);
        var result = _analyzer.Analyze(_tmp);
        result.MenuLocations.Should().BeEmpty();
    }
}
