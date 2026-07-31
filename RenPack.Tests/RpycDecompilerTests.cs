using System.Collections;
using FluentAssertions;
using Razorvine.Pickle.Objects;
using RenPack.Services;
using Xunit;

namespace RenPack.Tests;

/// <summary>
/// Tests für den Basic-Decompiler. Der Reader-Pfad ist an echten Ren'Py-8.4.
/// rpyc-Dateien manuell verifiziert; hier testen wir gezielt den AST-Writer
/// mit synthetischen <see cref="ClassDict"/>-Instanzen — der Writer ist der
/// Teil, der bei neuen Ren'Py-Versionen als erstes brechen kann.
/// </summary>
public sealed class RpycDecompilerTests
{
    private readonly RenpyRpycDecompiler _dec = new();

    private static ClassDict Node(string className, params (string key, object? value)[] fields)
    {
        var cd = new ClassDict("renpy.ast", className[(className.LastIndexOf('.') + 1)..]);
        // ClassName wird über den Modul-/Namen-Konstruktor gesetzt.
        // Für unsere Tests reicht das — der Decompiler prüft ClassDict.ClassName.
        foreach (var (k, v) in fields) cd[k] = v;
        return cd;
    }

    [Fact]
    public void Emits_say_with_who_and_what()
    {
        var script = new object[]
        {
            Node("renpy.ast.Say", ("what", "Hallo Welt"), ("who", "sophia")),
        };
        var text = _dec.Decompile(script);
        text.Should().Contain("sophia \"Hallo Welt\"");
    }

    [Fact]
    public void Emits_narrator_say_without_who()
    {
        var script = new object[]
        {
            Node("renpy.ast.Say", ("what", "Ein Beobachter spricht.")),
        };
        var text = _dec.Decompile(script);
        text.Should().Contain("\"Ein Beobachter spricht.\"");
        text.Should().NotContain(" \"Ein Beobachter spricht.\"" ); // kein whoSpace davor
    }

    [Fact]
    public void Escapes_quotes_in_say_text()
    {
        var script = new object[]
        {
            Node("renpy.ast.Say", ("what", "Er sagte \"Hallo\".")),
        };
        var text = _dec.Decompile(script);
        text.Should().Contain(@"\""Hallo\""");
    }

    [Fact]
    public void Emits_label_with_indented_block()
    {
        var script = new object[]
        {
            Node("renpy.ast.Label",
                ("_name", "start"),
                ("block", new ArrayList { Node("renpy.ast.Say", ("what", "Los!")) })),
        };
        var text = _dec.Decompile(script);
        text.Should().Contain("label start:");
        text.Should().Contain("    \"Los!\"");
    }

    [Fact]
    public void Emits_if_elif_else_chain()
    {
        var script = new object[]
        {
            Node("renpy.ast.If", ("entries", new ArrayList
            {
                new object[] { "money > 0", new ArrayList { Node("renpy.ast.Say", ("what", "Reich!")) } },
                new object[] { "money == 0", new ArrayList { Node("renpy.ast.Say", ("what", "Pleite.")) } },
                new object[] { "True",       new ArrayList { Node("renpy.ast.Say", ("what", "Fallback.")) } },
            })),
        };
        var text = _dec.Decompile(script);
        text.Should().Contain("if money > 0:");
        text.Should().Contain("elif money == 0:");
        text.Should().Contain("else:");
    }

    [Fact]
    public void Emits_menu_with_choice_captions_and_blocks()
    {
        var script = new object[]
        {
            Node("renpy.ast.Menu", ("items", new ArrayList
            {
                new object[] { "Ja", "True", new ArrayList { Node("renpy.ast.Jump", ("target", "ja")) } },
                new object[] { "Nein", "money < 5", new ArrayList { Node("renpy.ast.Jump", ("target", "nein")) } },
            })),
        };
        var text = _dec.Decompile(script);
        text.Should().Contain("menu:");
        text.Should().Contain("\"Ja\":");
        text.Should().Contain("\"Nein\" if money < 5:");
        text.Should().Contain("jump ja");
    }

    [Fact]
    public void Emits_python_single_line_as_dollar_shortcut()
    {
        var pyCode = new ClassDict("renpy.ast", "PyCode");
        // PyCode-Muster: __args__[0] als source (Direkt-Fall aus REDUCE)
        pyCode["__args__"] = new object[] { "money = 100" };
        var script = new object[]
        {
            Node("renpy.ast.Python", ("code", pyCode)),
        };
        var text = _dec.Decompile(script);
        text.Should().Contain("$ money = 100");
    }

    [Fact]
    public void Emits_return_without_expression()
    {
        var script = new object[]
        {
            Node("renpy.ast.Return"),
        };
        var text = _dec.Decompile(script);
        text.Should().MatchRegex(@"^return\s*$|(\breturn\s*$)");
    }

    [Fact]
    public void Unknown_node_falls_back_to_comment_marker()
    {
        var script = new object[]
        {
            Node("renpy.ast.SomethingWeird"),
        };
        var text = _dec.Decompile(script);
        text.Should().Contain("# <unsupported:");
        text.Should().Contain("SomethingWeird");
    }

    [Fact]
    public void Pyexpr_wrapper_is_unwrapped_to_source_string()
    {
        var expr = new ClassDict("renpy.astsupport", "PyExpr");
        expr["__args__"] = new object[] { "player_name", "game/story.rpy", 42 };

        var script = new object[]
        {
            Node("renpy.ast.Jump", ("target", expr), ("expression", true)),
        };
        var text = _dec.Decompile(script);
        text.Should().Contain("jump expression player_name");
    }
}
