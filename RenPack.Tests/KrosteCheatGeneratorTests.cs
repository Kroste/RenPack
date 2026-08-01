using FluentAssertions;
using RenPack.Services.Modding;
using Xunit;

namespace RenPack.Tests;

/// <summary>Tests fuer den Cheat-Menu-Generator: sowohl die
/// Kandidatenauswahl (welche Store-Vars kommen in den Screen?) als auch
/// die Ren'Py-Screen-Datei-Struktur.</summary>
public sealed class KrosteCheatGeneratorTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(),
        $"renpack-cheat-tests-{Guid.NewGuid():N}");
    private readonly KrosteCheatGenerator _gen = new();

    public KrosteCheatGeneratorTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private static ModAnalysis MakeAnalysis(
        IReadOnlyList<RpyStoreVariable>? vars = null,
        IReadOnlyList<RpyChoice>? choices = null,
        Dictionary<string, IReadOnlyList<VarConsumer>>? consumers = null) =>
        new(
            Choices: choices ?? Array.Empty<RpyChoice>(),
            StoreVariables: vars ?? Array.Empty<RpyStoreVariable>(),
            Characters: Array.Empty<RpyCharacter>(),
            AnalyzedFiles: Array.Empty<string>(),
            VariableConsumers: consumers ?? new Dictionary<string, IReadOnlyList<VarConsumer>>(),
            MenuLocations: Array.Empty<RpyMenuLocation>());

    // ---- Candidate-Auswahl -----------------------------------------------

    [Fact]
    public void Selects_only_int_float_bool_vars_that_are_used()
    {
        // string und expr fallen raus; int/float/bool die nirgends verwendet
        // werden fallen ebenfalls raus (ScoreLine 0).
        var choice = new RpyChoice("f.rpy", 1, "l", 0, 1, 0, "text", null,
            new[] { new VarDelta("love", "+=", "1") });
        var analysis = MakeAnalysis(
            vars: new[]
            {
                new RpyStoreVariable("love", "0", "int"),
                new RpyStoreVariable("player_name", "\"\"", "str"),       // string — raus
                new RpyStoreVariable("unused_flag", "False", "bool"),     // ungenutzt — raus
                new RpyStoreVariable("some_expr", "renpy.random.choice([1,2])", "expr"),  // expr — raus
            },
            choices: new[] { choice });
        var candidates = KrosteCheatGenerator.SelectCheatCandidates(analysis);
        candidates.Should().ContainSingle().Which.Name.Should().Be("love");
    }

    [Fact]
    public void Bool_vars_referenced_in_conditions_qualify()
    {
        var analysis = MakeAnalysis(
            vars: new[] { new RpyStoreVariable("day22_visited", "False", "bool") },
            consumers: new Dictionary<string, IReadOnlyList<VarConsumer>>
            {
                ["day22_visited"] = new[]
                {
                    new VarConsumer("day22.rpy", 5, "start",
                        VarConsumerKind.Condition, "day22_visited"),
                },
            });
        var candidates = KrosteCheatGenerator.SelectCheatCandidates(analysis);
        candidates.Should().ContainSingle();
        candidates[0].Kind.Should().Be("bool");
    }

    [Fact]
    public void Ranks_by_delta_frequency_first()
    {
        // 2 Deltas fuer "love" (score = 2*2 = 4), 1 Consumer fuer "respect" (score = 1).
        var choices = new[]
        {
            new RpyChoice("f.rpy", 1, "l", 0, 1, 0, "a", null,
                new[] { new VarDelta("love", "+=", "1") }),
            new RpyChoice("f.rpy", 2, "l", 0, 1, 1, "b", null,
                new[] { new VarDelta("love", "+=", "2") }),
        };
        var analysis = MakeAnalysis(
            vars: new[]
            {
                new RpyStoreVariable("love", "0", "int"),
                new RpyStoreVariable("respect", "0", "int"),
            },
            choices: choices,
            consumers: new Dictionary<string, IReadOnlyList<VarConsumer>>
            {
                ["respect"] = new[]
                {
                    new VarConsumer("f.rpy", 10, "l", VarConsumerKind.Condition, "respect"),
                },
            });
        var candidates = KrosteCheatGenerator.SelectCheatCandidates(analysis);
        candidates[0].Name.Should().Be("love", "hoehere Delta-Frequenz");
    }

    // ---- Screen-Struktur -------------------------------------------------

    [Fact]
    public void Generates_screen_with_f11_keymap_and_adjust_functions()
    {
        var choice = new RpyChoice("f.rpy", 1, "l", 0, 1, 0, "text", null,
            new[] { new VarDelta("love", "+=", "1") });
        var analysis = MakeAnalysis(
            vars: new[] { new RpyStoreVariable("love", "0", "int") },
            choices: new[] { choice });
        var path = _gen.Generate(_tmp, analysis);
        var content = File.ReadAllText(path);

        // Screen + Keymap
        content.Should().Contain("screen krostemod_cheat");
        content.Should().Contain("K_F11");
        content.Should().Contain("K_ESCAPE");
        content.Should().Contain("krostemod_cheat_toggle");

        // Adjust-Buttons fuer int
        content.Should().Contain("krostemod_cheat_adjust");
        content.Should().Contain("-10");
        content.Should().Contain("+10");

        // Cheat-Vars-Tabelle
        content.Should().Contain("krostemod_cheat_vars");
        content.Should().Contain("\"love\"");
        content.Should().Contain("\"int\"");

        // Reset-Function + Escape-Helper
        content.Should().Contain("krostemod_cheat_reset");
        content.Should().Contain("krostemod_cheat_escape");
    }

    [Fact]
    public void Bool_var_gets_toggle_button_not_numeric_adjust()
    {
        var analysis = MakeAnalysis(
            vars: new[] { new RpyStoreVariable("flag", "False", "bool") },
            consumers: new Dictionary<string, IReadOnlyList<VarConsumer>>
            {
                ["flag"] = new[]
                {
                    new VarConsumer("f.rpy", 1, "l", VarConsumerKind.Condition, "flag"),
                },
            });
        var path = _gen.Generate(_tmp, analysis);
        var content = File.ReadAllText(path);
        content.Should().Contain("\"bool\"");
        // Der Toggle-Button ist konditional im Screen definiert
        content.Should().Contain("if ce_kind == \"bool\":");
        content.Should().Contain("Toggle");
    }

    [Fact]
    public void Skips_generation_gracefully_when_no_candidates_exist()
    {
        // Kein StoreVariables mit relevanten Typen + kein Choice → leerer
        // Cheat-Vars-Array. Screen wird trotzdem erzeugt, ist nur leer.
        var path = _gen.Generate(_tmp, MakeAnalysis());
        var content = File.ReadAllText(path);
        content.Should().Contain("krostemod_cheat_vars = [");
        content.Should().Contain("]"); // Liste geschlossen — leer, aber valide Python
    }

    // ---- v0.10.1: Overlay-Icon (immer sichtbar oben rechts) --------------

    [Fact]
    public void Deploys_anonymous_mask_icon_png_and_wires_overlay_screen()
    {
        _gen.Generate(_tmp, MakeAnalysis());
        var iconPath = Path.Combine(_tmp, KrosteCheatGenerator.CheatIconFileName);
        File.Exists(iconPath).Should().BeTrue();
        // PNG-Magic
        var bytes = File.ReadAllBytes(iconPath);
        bytes.Should().StartWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
    }

    [Fact]
    public void Overlay_screen_registers_as_always_on_and_uses_imagebutton()
    {
        var path = _gen.Generate(_tmp, MakeAnalysis());
        var content = File.ReadAllText(path);

        // Overlay-Screen mit imagebutton (nicht conditional!) — Cheat-Icon
        // ist immer sichtbar, im Gegensatz zum Info-\"!\".
        content.Should().Contain("screen krostemod_cheat_overlay");
        content.Should().Contain("imagebutton");
        content.Should().Contain("krostemod_cheat.png");
        content.Should().Contain("ToggleScreen(\"krostemod_cheat\")");

        // Registration im overlay_screens-List
        content.Should().Contain("config.overlay_screens.append('krostemod_cheat_overlay')");

        // Positioning: yalign 0.09 = direkt unter dem Info-\"!\"-Icon
        // (das bei yalign 0.02 sitzt) → keine Kollision.
        content.Should().Contain("yalign 0.09");
    }
}
