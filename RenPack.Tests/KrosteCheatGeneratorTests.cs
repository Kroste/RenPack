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
            MenuLocations: Array.Empty<RpyMenuLocation>(),
            SayStatements: Array.Empty<RpySayStatement>());

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

    [Fact]
    public void Includes_delta_only_vars_like_character_container_attributes()
    {
        // Character-Container-Attribute wie fcs.morality haben KEINE
        // StoreVariable (kein `default fcs.morality = 0`) — sie werden
        // nur ueber Delta erfasst (`$ fcs.update("morality", 1)`).
        // Der Cheat-Generator muss sie trotzdem als Kandidaten
        // aufnehmen — sonst fehlen bei Boundaries of Morality 20+ Stats.
        var choices = new[]
        {
            new RpyChoice("f.rpy", 1, "l", 0, 1, 0, "help", null,
                new[] { new VarDelta("fcs.morality", "+=", "1") }),
            new RpyChoice("f.rpy", 2, "l", 0, 1, 1, "ignore", null,
                new[] { new VarDelta("fcs.intrigue", "+=", "-1") }),
        };
        var analysis = MakeAnalysis(choices: choices);
        var candidates = KrosteCheatGenerator.SelectCheatCandidates(analysis);
        candidates.Select(c => c.Name).Should().Contain("fcs.morality");
        candidates.Select(c => c.Name).Should().Contain("fcs.intrigue");
        candidates.Single(c => c.Name == "fcs.morality").Kind.Should().Be("int");
        // Default fuer Delta-only-int: "0"
        candidates.Single(c => c.Name == "fcs.morality").DefaultValue.Should().Be("0");
    }

    // ---- v0.9 Pack B: Custom-Value-Input Prompt ---------------------------

    [Fact]
    public void Set_prompt_screen_and_helpers_are_emitted()
    {
        var choice = new RpyChoice("f.rpy", 1, "l", 0, 1, 0, "text", null,
            new[] { new VarDelta("love", "+=", "1") });
        var analysis = MakeAnalysis(
            vars: new[] { new RpyStoreVariable("love", "0", "int") },
            choices: new[] { choice });
        var path = _gen.Generate(_tmp, analysis);
        var content = File.ReadAllText(path);

        // Per-Row Set…-Button (nur bei nicht-bool)
        content.Should().Contain("\"Set…\""); // "Set…"

        // Modal-Screen
        content.Should().Contain("screen krostemod_cheat_set_prompt(name, kind):");
        content.Should().Contain("zorder 230");
        content.Should().Contain("K_RETURN");

        // Type-Coercion in apply_prompt
        content.Should().Contain("krostemod_cheat_apply_prompt");
        content.Should().Contain("if kind == 'int': val = int(raw)");
        content.Should().Contain("elif kind == 'float': val = float(raw)");
    }

    // ---- Container-Gruppierung (v0.9-Groups) ------------------------------

    [Fact]
    public void Builds_single_group_when_no_profile_or_no_containers()
    {
        // Ohne Profile ODER Profile ohne Container-Flag: alles in einer
        // Gruppe mit leerem Label — Screen rendert dann ohne Section-Header.
        var vars = new[]
        {
            new CheatCandidate("love", "int", "0"),
            new CheatCandidate("respect", "int", "0"),
        };
        var groups = KrosteCheatGenerator.BuildGroups(vars, profile: null);
        groups.Should().ContainSingle();
        groups[0].Label.Should().BeEmpty();
        groups[0].Vars.Should().HaveCount(2);
    }

    [Fact]
    public void Groups_by_container_prefix_when_profile_signals_containers()
    {
        // Container-Profile + genug dotted Vars → pro Prefix eine Gruppe,
        // flat Vars in „General" (leeres Label) am Ende.
        var vars = new[]
        {
            new CheatCandidate("fcs.morality", "int", "0"),
            new CheatCandidate("fcs.desire", "int", "0"),
            new CheatCandidate("fcs.intrigue", "int", "0"),
            new CheatCandidate("samantha.love", "int", "0"),
            new CheatCandidate("keys", "int", "0"),
        };
        var profile = new GameProfile(
            MenuScreenCandidates: new[] { "choice" },
            HasCharacterContainers: true,
            DominantChoiceStyle: ChoiceStyle.Mixed,
            TranslationLanguages: Array.Empty<string>(),
            DetectedTitle: null);
        var groups = KrosteCheatGenerator.BuildGroups(vars, profile);

        groups.Should().HaveCount(3);
        groups[0].Label.Should().Be("fcs");
        groups[0].Vars.Should().HaveCount(3);
        groups[1].Label.Should().Be("samantha");
        groups[1].Vars.Should().ContainSingle();
        groups[2].Label.Should().BeEmpty(); // General-Gruppe
        groups[2].Vars.Should().ContainSingle().Which.Name.Should().Be("keys");
    }

    [Fact]
    public void Falls_back_to_flat_when_too_few_dotted_vars()
    {
        // Weniger als 3 dotted-Vars → keine Gruppierung, wuerde nur
        // visueller Overhead sein.
        var vars = new[]
        {
            new CheatCandidate("fcs.morality", "int", "0"),
            new CheatCandidate("fcs.desire", "int", "0"),
            new CheatCandidate("love", "int", "0"),
        };
        var profile = new GameProfile(
            MenuScreenCandidates: new[] { "choice" },
            HasCharacterContainers: true,
            DominantChoiceStyle: ChoiceStyle.Mixed,
            TranslationLanguages: Array.Empty<string>(),
            DetectedTitle: null);
        var groups = KrosteCheatGenerator.BuildGroups(vars, profile);
        groups.Should().ContainSingle();
        groups[0].Vars.Should().HaveCount(3);
    }

    [Fact]
    public void Screen_iterates_groups_and_shows_section_headers_when_grouped()
    {
        // 4 Vars in 2 verschiedenen Containern → 2 Gruppen → Section-Headers.
        var choices = new List<RpyChoice>
        {
            new("f.rpy", 1, "l", 0, 1, 0, "c0", null,
                new[] { new VarDelta("fcs.morality", "+=", "1") }),
            new("f.rpy", 2, "l", 0, 1, 1, "c1", null,
                new[] { new VarDelta("fcs.desire", "+=", "1") }),
            new("f.rpy", 3, "l", 0, 1, 2, "c2", null,
                new[] { new VarDelta("samantha.love", "+=", "1") }),
            new("f.rpy", 4, "l", 0, 1, 3, "c3", null,
                new[] { new VarDelta("samantha.lust", "+=", "1") }),
        };
        var profile = new GameProfile(
            MenuScreenCandidates: new[] { "choice" },
            HasCharacterContainers: true,
            DominantChoiceStyle: ChoiceStyle.Mixed,
            TranslationLanguages: Array.Empty<string>(),
            DetectedTitle: "Test");
        var path = _gen.Generate(_tmp, MakeAnalysis(choices: choices), profile);
        var content = File.ReadAllText(path);

        content.Should().Contain("krostemod_cheat_groups");
        content.Should().Contain("for group_label, group_entries in krostemod_cheat_groups:");
        content.Should().Contain("[group_label]"); // Section-Header wird gerendert
    }

    [Fact]
    public void Screen_omits_section_headers_when_flat()
    {
        var choice = new RpyChoice("f.rpy", 1, "l", 0, 1, 0, "text", null,
            new[] { new VarDelta("love", "+=", "1") });
        var analysis = MakeAnalysis(
            vars: new[] { new RpyStoreVariable("love", "0", "int") },
            choices: new[] { choice });
        var path = _gen.Generate(_tmp, analysis); // kein Profile → keine Gruppierung
        var content = File.ReadAllText(path);

        content.Should().Contain("krostemod_cheat_groups");
        // Screen-Loop existiert trotzdem, aber Section-Header wird nicht emitted.
        content.Should().NotContain("[group_label]");
    }

    [Fact]
    public void Vars_within_container_group_are_sorted_by_score_not_alphabetical()
    {
        // Score-Sortierung ist schon im SelectCheatCandidates verdrahtet
        // (ThenByDescending(score)), aber wir wollen sicherstellen dass
        // sie durch die Gruppierung nicht kaputt geht. Bei alphabetischer
        // Sortierung waere „fcs.desire" vor „fcs.morality" — wir wollen
        // aber die haeufiger-modifizierte Var (morality mit 3 Deltas)
        // vor der selten-modifizierten (desire mit 1 Delta).
        var choices = new List<RpyChoice>
        {
            new("f.rpy", 1, "l", 0, 1, 0, "c0", null,
                new[] { new VarDelta("fcs.morality", "+=", "1") }),
            new("f.rpy", 2, "l", 0, 1, 1, "c1", null,
                new[] { new VarDelta("fcs.morality", "+=", "1") }),
            new("f.rpy", 3, "l", 0, 1, 2, "c2", null,
                new[] { new VarDelta("fcs.morality", "+=", "1") }),
            new("f.rpy", 4, "l", 0, 1, 3, "c3", null,
                new[] { new VarDelta("fcs.desire", "+=", "1") }),
            // Zweite Gruppe damit BuildGroups tatsaechlich gruppiert
            new("f.rpy", 5, "l", 0, 1, 4, "c4", null,
                new[] { new VarDelta("samantha.love", "+=", "1") }),
            new("f.rpy", 6, "l", 0, 1, 5, "c5", null,
                new[] { new VarDelta("samantha.lust", "+=", "1") }),
        };
        var profile = new GameProfile(
            MenuScreenCandidates: new[] { "choice" },
            HasCharacterContainers: true,
            DominantChoiceStyle: ChoiceStyle.Mixed,
            TranslationLanguages: Array.Empty<string>(),
            DetectedTitle: null);
        var candidates = KrosteCheatGenerator.SelectCheatCandidates(MakeAnalysis(choices: choices));
        var groups = KrosteCheatGenerator.BuildGroups(candidates, profile);

        var fcsGroup = groups.Single(g => g.Label == "fcs");
        // morality (3 Deltas, score=6) muss vor desire (1 Delta, score=2) stehen
        fcsGroup.Vars[0].Name.Should().Be("fcs.morality");
        fcsGroup.Vars[1].Name.Should().Be("fcs.desire");
    }

    [Fact]
    public void Sorts_numeric_vars_before_bool_flags()
    {
        // User-Praeferenz: die meisten benoetigten Cheats sind Zahlen —
        // bool-Flags sind sekundaer. Bei begrenzter Slot-Zahl (MaxCheatVars)
        // sollen ints/floats zuerst kommen.
        var analysis = MakeAnalysis(
            vars: new[]
            {
                new RpyStoreVariable("flag_a", "False", "bool"),
                new RpyStoreVariable("money", "0", "int"),
                new RpyStoreVariable("flag_b", "True", "bool"),
                new RpyStoreVariable("love", "0", "int"),
            },
            consumers: new Dictionary<string, IReadOnlyList<VarConsumer>>
            {
                ["flag_a"] = new[] { new VarConsumer("f.rpy", 1, "l", VarConsumerKind.Condition, "if flag_a") },
                ["money"] = new[] { new VarConsumer("f.rpy", 2, "l", VarConsumerKind.Condition, "if money > 0") },
                ["flag_b"] = new[] { new VarConsumer("f.rpy", 3, "l", VarConsumerKind.Condition, "if flag_b") },
                ["love"] = new[] { new VarConsumer("f.rpy", 4, "l", VarConsumerKind.Condition, "if love") },
            });
        var candidates = KrosteCheatGenerator.SelectCheatCandidates(analysis);
        var firstBoolIdx = candidates.ToList().FindIndex(c => c.Kind == "bool");
        var lastIntIdx = candidates.ToList().FindLastIndex(c => c.Kind == "int");
        firstBoolIdx.Should().BeGreaterThan(lastIntIdx,
            "int-Kandidaten muessen vor bool-Kandidaten stehen");
    }
}
