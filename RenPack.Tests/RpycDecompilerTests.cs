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
    public void Init_with_single_define_is_flattened_to_bare_statement()
    {
        // Ren'Py-Compiler-Ausgabe: define wird immer in Init(0, [Define]) verpackt.
        // Wir wollen im Output einfach `define x = ...` sehen, keine Init-Zeile.
        var init = Node("renpy.ast.Init",
            ("priority", 0),
            ("block", new ArrayList
            {
                Node("renpy.ast.Define", ("varname", "money"), ("code", "100")),
            }));
        var text = _dec.Decompile(new object[] { init });
        text.Should().Contain("define money = 100");
        text.Should().NotContain("init 0:");
    }

    [Fact]
    public void Empty_init_block_gets_pass_so_renpy_parser_accepts_it()
    {
        // Init mit Block, in dem nur unbekannte Nodes stehen — Ren'Py sieht das
        // sonst als leer und wirft "init statement expects a non-empty block".
        var init = Node("renpy.ast.Init", ("priority", 500),
            ("block", new ArrayList { Node("renpy.ast.SomethingUnknown") }));
        var text = _dec.Decompile(new object[] { init });
        text.Should().Contain("init 500:");
        text.Should().Contain("    # <unsupported:");
        text.Should().Contain("    pass"); // Fallback für Ren'Py-Parser
    }

    [Fact]
    public void Empty_label_block_gets_pass()
    {
        var label = Node("renpy.ast.Label", ("_name", "empty_label"),
            ("block", new ArrayList()));
        var text = _dec.Decompile(new object[] { label });
        text.Should().Contain("label empty_label:");
        text.Should().Contain("    pass");
    }

    [Fact]
    public void Image_with_atl_block_emits_atl_statements()
    {
        // Ein realistischer Blink-Effekt: initial-alpha, pause, ease in/out.
        var pauseStmt = new ClassDict("renpy.atl", "RawMultipurpose");
        pauseStmt["warper"] = "pause"; pauseStmt["duration"] = "0.3";
        pauseStmt["expressions"] = new ArrayList(); pauseStmt["properties"] = new ArrayList();

        var setAlpha = new ClassDict("renpy.atl", "RawMultipurpose");
        setAlpha["warper"] = null; setAlpha["duration"] = "0";
        setAlpha["expressions"] = new ArrayList();
        setAlpha["properties"] = new ArrayList { new object[] { "alpha", "0.0" } };

        var easeIn = new ClassDict("renpy.atl", "RawMultipurpose");
        easeIn["warper"] = "ease"; easeIn["duration"] = "0.05";
        easeIn["expressions"] = new ArrayList();
        easeIn["properties"] = new ArrayList { new object[] { "alpha", "1.0" } };

        var atl = new ClassDict("renpy.atl", "RawBlock");
        atl["statements"] = new ArrayList { setAlpha, pauseStmt, easeIn };

        var img = Node("renpy.ast.Image",
            ("imgname", new object[] { "flash" }),
            ("atl", atl));
        var text = _dec.Decompile(new object[] { img });
        text.Should().Contain("image flash:");
        text.Should().Contain("    alpha 0.0");
        text.Should().Contain("    pause 0.3");
        text.Should().Contain("    ease 0.05 alpha 1.0");
    }

    [Fact]
    public void Atl_multipurpose_with_multiple_properties_emits_them_all()
    {
        var stmt = new ClassDict("renpy.atl", "RawMultipurpose");
        stmt["warper"] = "linear"; stmt["duration"] = "0.5";
        stmt["expressions"] = new ArrayList();
        stmt["properties"] = new ArrayList
        {
            new object[] { "xpos", "100" },
            new object[] { "ypos", "200" },
            new object[] { "alpha", "0.8" },
        };
        var block = new ClassDict("renpy.atl", "RawBlock");
        block["statements"] = new ArrayList { stmt };
        var img = Node("renpy.ast.Image",
            ("imgname", new object[] { "hero" }), ("atl", block));
        var text = _dec.Decompile(new object[] { img });
        text.Should().Contain("linear 0.5 xpos 100 ypos 200 alpha 0.8");
    }

    [Fact]
    public void Screen_with_widgets_and_conditionals_is_emitted_as_screen_language()
    {
        // Ein realistischer Mini-Screen: ein Frame mit vbox, text, textbutton
        // und einem if-Zweig.
        var textNode = new ClassDict("renpy.sl2.slast", "SLDisplayable");
        textNode["name"] = "text";
        textNode["positional"] = new ArrayList { "\"Hello\"" };
        textNode["keyword"] = new ArrayList();
        textNode["children"] = new ArrayList();

        var buttonNode = new ClassDict("renpy.sl2.slast", "SLDisplayable");
        buttonNode["name"] = "textbutton";
        buttonNode["positional"] = new ArrayList { "\"Start\"" };
        buttonNode["keyword"] = new ArrayList { new object[] { "action", "Start()" } };
        buttonNode["children"] = new ArrayList();

        var ifBlock = new ClassDict("renpy.sl2.slast", "SLBlock");
        ifBlock["keyword"] = new ArrayList();
        ifBlock["children"] = new ArrayList { textNode };

        var ifNode = new ClassDict("renpy.sl2.slast", "SLIf");
        ifNode["entries"] = new ArrayList { new object[] { "condition", ifBlock } };

        var vboxNode = new ClassDict("renpy.sl2.slast", "SLDisplayable");
        vboxNode["name"] = "vbox";
        vboxNode["positional"] = new ArrayList();
        vboxNode["keyword"] = new ArrayList();
        vboxNode["children"] = new ArrayList { textNode, buttonNode, ifNode };

        var slScreen = new ClassDict("renpy.sl2.slast", "SLScreen");
        slScreen["name"] = "main_menu";
        slScreen["keyword"] = new ArrayList { new object[] { "modal", "True" } };
        slScreen["children"] = new ArrayList { vboxNode };

        var astScreen = Node("renpy.ast.Screen", ("screen", slScreen));
        var text = _dec.Decompile(new object[] { astScreen });

        text.Should().Contain("screen main_menu:");
        text.Should().Contain("modal True");
        text.Should().Contain("vbox:");
        text.Should().Contain("text \"Hello\"");
        text.Should().Contain("textbutton \"Start\":");
        text.Should().Contain("action Start()");
        text.Should().Contain("if condition:");
    }

    [Fact]
    public void Style_declaration_is_emitted_with_properties()
    {
        var props = new Hashtable
        {
            ["color"] = "\"#ffffff\"",
            ["size"] = "24",
        };
        var style = Node("renpy.ast.Style",
            ("style_name", "chat_header"),
            ("parent", "default"),
            ("properties", props));
        var text = _dec.Decompile(new object[] { style });
        text.Should().Contain("style chat_header is default:");
        text.Should().Contain("color \"#ffffff\"");
        text.Should().Contain("size 24");
    }

    [Fact]
    public void Transform_declaration_uses_atl_writer_for_body()
    {
        var pauseStmt = new ClassDict("renpy.atl", "RawMultipurpose");
        pauseStmt["warper"] = "pause"; pauseStmt["duration"] = "0.5";
        pauseStmt["expressions"] = new ArrayList(); pauseStmt["properties"] = new ArrayList();
        var block = new ClassDict("renpy.atl", "RawBlock");
        block["statements"] = new ArrayList { pauseStmt };

        var tr = Node("renpy.ast.Transform", ("varname", "delayed"), ("atl", block));
        var text = _dec.Decompile(new object[] { tr });
        text.Should().Contain("transform delayed:");
        text.Should().Contain("    pause 0.5");
    }

    [Fact]
    public void Atl_unknown_raw_class_becomes_comment_with_pass()
    {
        var stmt = new ClassDict("renpy.atl", "RawSomethingNew");
        var block = new ClassDict("renpy.atl", "RawBlock");
        block["statements"] = new ArrayList { stmt };
        var img = Node("renpy.ast.Image", ("imgname", new object[] { "x" }), ("atl", block));
        var text = _dec.Decompile(new object[] { img });
        text.Should().Contain("# <unsupported ATL: renpy.atl.RawSomethingNew>");
        text.Should().Contain("    pass"); // damit der image-Block valide bleibt
    }

    [Fact]
    public void Image_node_emits_image_statement()
    {
        var pyExpr = new ClassDict("renpy.astsupport", "PyExpr");
        pyExpr["__args__"] = new object[] { "\"images/hero.png\"" };
        var img = Node("renpy.ast.Image",
            ("imgname", new object[] { "hero" }),
            ("code", pyExpr));
        var text = _dec.Decompile(new object[] { img });
        text.Should().Contain("image hero = \"images/hero.png\"");
    }

    [Fact]
    public void Init_with_multiple_statements_keeps_init_wrapper()
    {
        var init = Node("renpy.ast.Init",
            ("priority", 0),
            ("block", new ArrayList
            {
                Node("renpy.ast.Define", ("varname", "a"), ("code", "1")),
                Node("renpy.ast.Define", ("varname", "b"), ("code", "2")),
            }));
        var text = _dec.Decompile(new object[] { init });
        text.Should().Contain("init 0:");
        text.Should().Contain("    define a = 1");
        text.Should().Contain("    define b = 2");
    }

    [Fact]
    public void Init_with_nonzero_priority_keeps_init_wrapper()
    {
        var init = Node("renpy.ast.Init",
            ("priority", -2),
            ("block", new ArrayList { Node("renpy.ast.Define", ("varname", "x"), ("code", "1")) }));
        var text = _dec.Decompile(new object[] { init });
        text.Should().Contain("init -2:");
    }

    [Fact]
    public void Call_followed_by_auto_sync_label_omits_the_label()
    {
        // Ren'Py-Compiler fügt nach `call X` ein `Label(_call_X)` + `Pass` ein.
        // Beide sollen weg — im Output nur `call X`.
        var script = new object[]
        {
            Node("renpy.ast.Call", ("label", "target")),
            Node("renpy.ast.Label", ("_name", "_call_target")),
            Node("renpy.ast.Pass"),
            Node("renpy.ast.Say", ("what", "danach")),
        };
        var text = _dec.Decompile(script);
        text.Should().Contain("call target");
        text.Should().NotContain("_call_target");
        text.Should().Contain("\"danach\"");
    }

    [Fact]
    public void Call_with_from_clause_label_emits_from_syntax()
    {
        // Wenn der User `call X from Y` geschrieben hat, generiert der Compiler
        // `Label(_call_Y)` (nicht `_call_X`). Wir hängen `from _call_Y` an den Call.
        var script = new object[]
        {
            Node("renpy.ast.Call", ("label", "target")),
            Node("renpy.ast.Label", ("_name", "_call_custom_name")),
            Node("renpy.ast.Pass"),
        };
        var text = _dec.Decompile(script);
        text.Should().Contain("call target from _call_custom_name");
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
