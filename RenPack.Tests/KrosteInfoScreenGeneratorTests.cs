using FluentAssertions;
using RenPack.Services.Modding;
using Xunit;

namespace RenPack.Tests;

/// <summary>Tests fuer den F10-Info-Screen-Generator. Wir testen den
/// erzeugten .rpy-Text: enthaelt er Impact-Dict, Screen-Definition, Keymap-
/// Binding fuer F10. Ren'Py-Syntax koennen wir hier nicht ausfuehren, aber
/// die Struktur pruefen wir syntaktisch.</summary>
public sealed class KrosteInfoScreenGeneratorTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(),
        $"renpack-info-tests-{Guid.NewGuid():N}");
    private readonly KrosteInfoScreenGenerator _gen = new();

    public KrosteInfoScreenGeneratorTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private static ModAnalysis MakeAnalysis(
        IReadOnlyList<RpyStoreVariable>? vars = null,
        Dictionary<string, IReadOnlyList<VarConsumer>>? consumers = null,
        IReadOnlyList<RpyMenuLocation>? menus = null) =>
        new(
            Choices: Array.Empty<RpyChoice>(),
            StoreVariables: vars ?? Array.Empty<RpyStoreVariable>(),
            Characters: Array.Empty<RpyCharacter>(),
            AnalyzedFiles: Array.Empty<string>(),
            VariableConsumers: consumers ?? new Dictionary<string, IReadOnlyList<VarConsumer>>(),
            MenuLocations: menus ?? Array.Empty<RpyMenuLocation>());

    [Fact]
    public void Emits_rpy_file_with_screen_and_keymap()
    {
        var analysis = MakeAnalysis(
            vars: new[] { new RpyStoreVariable("love", "0", "int") },
            consumers: new Dictionary<string, IReadOnlyList<VarConsumer>>
            {
                ["love"] = new[]
                {
                    new VarConsumer("day22.rpy", 234, "day22_scene",
                        VarConsumerKind.Condition, "love >= 5"),
                },
            });
        var path = _gen.Generate(_tmp, analysis);
        var content = File.ReadAllText(path);

        // Impact-Dict mit love drin
        content.Should().Contain("krostemod_impact");
        content.Should().Contain("\"love\"");
        content.Should().Contain("\"day22.rpy\"");
        content.Should().Contain("234");
        content.Should().Contain("\"day22_scene\"");

        // Screen-Definition + F10-Keymap
        content.Should().Contain("screen krostemod_info");
        content.Should().Contain("K_F10");
        content.Should().Contain("K_ESCAPE");
        content.Should().Contain("config.keymap");
        content.Should().Contain("renpy.Keymap");

        // Helper-Function fuer Live-Werte
        content.Should().Contain("krostemod_get_value");
        content.Should().Contain("getattr(store,");
    }

    [Fact]
    public void Truncates_consumer_list_to_max_and_notes_overflow()
    {
        // 12 Consumers → nur 8 im Dict + "+4 more"-Kommentar.
        var many = Enumerable.Range(1, 12)
            .Select(i => new VarConsumer($"f{i}.rpy", i, $"lbl{i}",
                VarConsumerKind.Condition, "cond"))
            .Cast<VarConsumer>()
            .ToList();
        var analysis = MakeAnalysis(consumers: new Dictionary<string, IReadOnlyList<VarConsumer>>
        {
            ["popular_var"] = many,
        });
        var path = _gen.Generate(_tmp, analysis);
        var content = File.ReadAllText(path);
        content.Should().Contain("# +4 more consumer(s)");
        // Erste 8 sind drin
        content.Should().Contain("\"f1.rpy\"");
        content.Should().Contain("\"f8.rpy\"");
        // 9-12 nicht
        content.Should().NotContain("\"f9.rpy\"");
    }

    [Fact]
    public void Escapes_special_chars_in_python_strings()
    {
        // Ein Label-Name mit " oder \n wuerde die Python-Init-Zeile zerreissen.
        // PyStr muss escapen. Bei uns kommen solche Namen zwar nicht vor,
        // aber Snippet-Content ist wilder — z.B. `if x == "\"foo\""`.
        var analysis = MakeAnalysis(consumers: new Dictionary<string, IReadOnlyList<VarConsumer>>
        {
            ["x"] = new[]
            {
                new VarConsumer("f.rpy", 1, "lbl",
                    VarConsumerKind.Condition, "x == \"quoted\" and y"),
            },
        });
        var path = _gen.Generate(_tmp, analysis);
        var content = File.ReadAllText(path);
        // Sollte weder rohes " noch Zeilenumbruch mitten in der Init-Liste haben.
        content.Should().Contain("\\\"quoted\\\"");
    }

    [Fact]
    public void Variables_without_consumers_still_appear_when_declared_as_store()
    {
        // Selbst wenn eine Variable nirgends im if geprueft wird, will der
        // Spieler ihren Live-Wert sehen — daher: alle StoreVariables + alle
        // Consumer-Keys vereinen.
        var analysis = MakeAnalysis(
            vars: new[] { new RpyStoreVariable("unused_var", "0", "int") });
        var path = _gen.Generate(_tmp, analysis);
        var content = File.ReadAllText(path);
        content.Should().Contain("\"unused_var\"");
    }

    [Fact]
    public void Emits_renpy_text_escape_helper_for_dict_and_list_values()
    {
        // Regression-Test fuer v0.9.0-Bug (Sophia Parker):
        // repr({}) == '{}' — Ren'Py's Text-Tokenizer wirft "Empty text tag"
        // wenn '{}' rendered wird. Escape muss vor der Anzeige greifen.
        var path = _gen.Generate(_tmp, MakeAnalysis());
        var content = File.ReadAllText(path);
        // Escape-Helper muss im Init-Python-Block sein
        content.Should().Contain("def krostemod_escape");
        content.Should().Contain("'{{'");
        content.Should().Contain("'[['");
        // krostemod_get_value muss den Escape anwenden
        content.Should().Contain("return krostemod_escape(s)");
        // Snippet-Rendering muss auch escapen (entry[4])
        content.Should().Contain("krostemod_escape(entry[4])");
    }

    [Fact]
    public void Emits_menu_hint_overlay_screen_with_impact_dict()
    {
        // v0.9.3: kontextueller "!"-Button oben rechts wenn ein Menu laueft.
        var analysis = MakeAnalysis(menus: new[]
        {
            new RpyMenuLocation("scripts/day22.rpy", 42,
                new[] { "love", "respect" }),
        });
        var path = _gen.Generate(_tmp, analysis);
        var content = File.ReadAllText(path);

        // Impact-Dict mit (file, line) → [vars]
        content.Should().Contain("krostemod_menu_impact");
        content.Should().Contain("(\"scripts/day22.rpy\", 42)");
        content.Should().Contain("\"love\"");

        // Runtime-Detection: renpy.get_screen('choice') + get_filename_line
        content.Should().Contain("def krostemod_current_menu_vars");
        content.Should().Contain("renpy.get_filename_line");
        content.Should().Contain("renpy.get_screen('choice')");

        // Overlay-Screen mit dem "!"-Button
        content.Should().Contain("screen krostemod_menu_hint");
        content.Should().Contain("krostemod_menu_hint_visible()");
        content.Should().Contain("ToggleScreen(\"krostemod_context_info\")");

        // Context-Info-Screen
        content.Should().Contain("screen krostemod_context_info");
        content.Should().Contain("krostemod_current_menu_vars()");

        // Overlay-Registration
        content.Should().Contain("config.overlay_screens.append('krostemod_menu_hint')");
    }

    [Fact]
    public void Filter_input_uses_single_arg_callback_not_SetVariable()
    {
        // Regression-Test fuer v0.9.1-Bug (Sophia Parker):
        // `input changed SetVariable("x", _)` crashed mit
        // "takes 1 positional argument but 2 were given", weil der
        // Input-changed-Handler den Callback mit (new_text) aufruft.
        // Muss eine Python-Funktion sein, die genau 1 Argument nimmt.
        var path = _gen.Generate(_tmp, MakeAnalysis());
        var content = File.ReadAllText(path);
        content.Should().Contain("def krostemod_set_filter(new_text):");
        content.Should().Contain("input default krostemod_filter");
        content.Should().Contain("changed krostemod_set_filter");
        // Das alte kaputte Muster darf nicht mehr da sein
        content.Should().NotContain("SetVariable(\"krostemod_filter\", _)");
    }
}
