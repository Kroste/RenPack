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
    public void Define_with_named_store_prefixes_variable()
    {
        // Ren'Py speichert im `store`-Feld "store.gui" für `define gui.foo = …`.
        // Wenn wir das ignorieren, landet die Variable im falschen Namespace
        // und spätere `gui.foo`-Zugriffe werfen AttributeError.
        var def = Node("renpy.ast.Define",
            ("varname", "button_text_font"),
            ("store", "store.gui"),
            ("code", "gui.interface_text_font"));
        var text = _dec.Decompile(new object[] { def });
        text.Should().Contain("define gui.button_text_font = gui.interface_text_font");
        text.Should().NotContain("define button_text_font =");
    }

    [Fact]
    public void Define_without_store_prefix_stays_bare()
    {
        // "store" (der Default-Store) → nur der Varname.
        var def = Node("renpy.ast.Define",
            ("varname", "money"),
            ("store", "store"),
            ("code", "100"));
        var text = _dec.Decompile(new object[] { def });
        text.Should().Contain("define money = 100");
        text.Should().NotContain("store.money");
    }

    [Fact]
    public void Default_with_named_store_prefixes_variable()
    {
        // Analog zu define — default gui.foo = bar
        var def = Node("renpy.ast.Default",
            ("varname", "player_name"),
            ("store", "store.persistent"),
            ("code", "\"Alice\""));
        var text = _dec.Decompile(new object[] { def });
        text.Should().Contain("default persistent.player_name = \"Alice\"");
    }

    [Fact]
    public void Scene_with_python_expression_uses_expression_keyword()
    {
        // imspec = (name=(), expression="renpy.random.choice([…])", tag=None, …)
        var imspec = new object?[]
        {
            new object[] { },           // name-tuple leer
            "renpy.random.choice(['a', 'b'])", // expression
            null, null, "master", null, null,  // tag, at_list, layer, zorder, behind
        };
        var script = new object[]
        {
            Node("renpy.ast.Scene", ("imspec", imspec)),
        };
        var text = _dec.Decompile(script);
        text.Should().Contain("scene expression renpy.random.choice(['a', 'b'])");
        text.Should().NotContain("scene renpy.random.choice");
    }

    [Fact]
    public void Show_with_at_list_and_zorder_emits_at_and_zorder_clauses()
    {
        var imspec = new object?[]
        {
            new object[] { "hero", "happy" }, // name
            null, "hero_tag",                 // expression, tag
            new object[] { "left", "flip" },  // at_list
            "screens",                        // layer
            "5",                              // zorder
            new object[] { "menu" },          // behind
        };
        var script = new object[]
        {
            Node("renpy.ast.Show", ("imspec", imspec)),
        };
        var text = _dec.Decompile(script);
        text.Should().Contain("show hero happy as hero_tag at left, flip behind menu onlayer screens zorder 5");
    }

    [Fact]
    public void EarlyPython_uses_python_early_block_not_dollar_shortcut()
    {
        // Der $-Shortcut existiert NUR für `python:` ohne Modifier. Bei
        // EarlyPython (und bei hide/in) muss immer die Block-Form kommen.
        // Reihenfolge: `python early:` (Wort-Reihenfolge ist wichtig — Ren'Py
        // wirft "expected statement" bei `early python:`).
        var pyCode = new ClassDict("renpy.ast", "PyCode");
        pyCode["__args__"] = new object[] { "import sys" };
        var script = new object[]
        {
            Node("renpy.ast.EarlyPython", ("code", pyCode)),
        };
        var text = _dec.Decompile(script);
        text.Should().Contain("python early:");
        text.Should().Contain("    import sys");
        text.Should().NotContain("early python:");
        text.Should().NotContain("$ early");
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
    public void Emits_return_without_expression_inside_label()
    {
        // Standalone Return am Top-Level ist Compiler-Artefakt (Ren'Py haengt
        // implizit `return` als letztes File-Statement an) und wird v0.11.1
        // unterdrueckt. In einem Label bleibt `return` als echte User-Syntax.
        var script = new object[]
        {
            Node("renpy.ast.Label", ("_name", "reset"),
                ("block", new ArrayList { Node("renpy.ast.Return") })),
        };
        var text = _dec.Decompile(script);
        text.Should().Contain("label reset:");
        text.Should().Contain("    return");
    }

    [Fact]
    public void Suppresses_trailing_top_level_return_as_compiler_artefact()
    {
        // Ren'Py-Compiler haengt implizit einen leeren Return-Node an jede
        // .rpy an — der ist im User-Source nie da. Wir muessen ihn beim
        // Decompilen wieder unterdruecken.
        var script = new object[]
        {
            Node("renpy.ast.Say", ("what", "Hallo Welt")),
            Node("renpy.ast.Return"),
        };
        var text = _dec.Decompile(script);
        text.Should().Contain("\"Hallo Welt\"");
        text.Split('\n').Should().NotContain(l => l.Trim() == "return");
    }

    [Fact]
    public void Init_wrap_around_default_priority_screen_is_unwrapped()
    {
        // Ren'Py-Compiler wrapped `screen X: ...` implizit in `init 0:`.
        // Wir sollten das beim Decompilen wieder auspacken — sonst wird die
        // .rpy unlesbar mit doppelter Einrueckung ueberall.
        var screen = Node("renpy.ast.Screen",
            ("screen", new ClassDict("renpy.sl2.slast", "SLScreen")));
        var init = Node("renpy.ast.Init", ("priority", 0),
            ("block", new ArrayList { screen }));
        var text = _dec.Decompile(new object[] { init });
        text.Should().NotContain("init 0:");
    }

    [Fact]
    public void Init_wrap_around_default_priority_image_is_unwrapped()
    {
        // image hat Ren'Py-Default-Prio 500.
        var img = Node("renpy.ast.Image",
            ("imgname", new object[] { "hero" }),
            ("code", new ClassDict("renpy.ast", "PyCode")));
        var init = Node("renpy.ast.Init", ("priority", 500),
            ("block", new ArrayList { img }));
        var text = _dec.Decompile(new object[] { init });
        text.Should().NotContain("init 500:");
    }

    [Fact]
    public void Non_default_priority_screen_uses_compact_init_prefix()
    {
        // `init -500 screen X:` statt `init -500:\n  screen X:` — kompakter
        // Prefix wenn genau EIN Screen/Style/Transform im Block ist.
        var screen = Node("renpy.ast.Screen",
            ("screen", new ClassDict("renpy.sl2.slast", "SLScreen")));
        var init = Node("renpy.ast.Init", ("priority", -500),
            ("block", new ArrayList { screen }));
        var text = _dec.Decompile(new object[] { init });
        text.Should().Contain("init -500 screen");
        // NICHT die verschachtelte Form
        text.Should().NotContain("init -500:\n    screen");
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
    public void SlDisplayable_without_children_emits_keywords_inline_not_as_body()
    {
        // Widget ohne Kinder aber mit Keywords: die Keywords müssen inline
        // stehen, sonst interpretiert Ren'Py `at TRANSFORM(child)` als
        // ATL-Block-Statement (child wird als Displayable behandelt) und
        // wirft "Not a displayable: 0". Real in Boundaries_of_Morality:
        //   add Text(msg["text"]):
        //       at flying_transform(msg["slot_index"])
        // → Not a displayable: 0. Muss inline sein:
        //   add Text(msg["text"]) at flying_transform(msg["slot_index"])
        var addNode = new ClassDict("renpy.sl2.slast", "SLDisplayable");
        addNode["name"] = "add";
        addNode["positional"] = new ArrayList { "Text(\"msg\")" };
        addNode["keyword"] = new ArrayList
        {
            new object[] { "at", "flying_transform(0)" },
        };
        addNode["children"] = new ArrayList();

        var block = new ClassDict("renpy.sl2.slast", "SLBlock");
        block["keyword"] = new ArrayList();
        block["children"] = new ArrayList { addNode };
        var slScreen = new ClassDict("renpy.sl2.slast", "SLScreen");
        slScreen["name"] = "test"; slScreen["keyword"] = new ArrayList();
        slScreen["children"] = new ArrayList { block };
        var astScreen = Node("renpy.ast.Screen", ("screen", slScreen));
        var text = _dec.Decompile(new object[] { astScreen });

        text.Should().Contain("add Text(\"msg\") at flying_transform(0)");
        text.Should().NotContain("add Text(\"msg\"):");
    }

    [Fact]
    public void SlUse_with_block_emits_transclude_body()
    {
        // `use TARGET(args):` mit Block-Body — der Body wird via transclude im
        // gerufenen Screen an dessen `transclude`-Stelle eingefügt. Wenn wir
        // den Block verlieren, hat der gerufene Screen nichts zum Transcluden
        // und die eigentliche Content wird nicht gerendert.
        var child = new ClassDict("renpy.sl2.slast", "SLDisplayable");
        child["name"] = "text"; child["positional"] = new ArrayList { "\"Save-Grid\"" };
        child["keyword"] = new ArrayList(); child["children"] = new ArrayList();

        var block = new ClassDict("renpy.sl2.slast", "SLBlock");
        block["keyword"] = new ArrayList();
        block["children"] = new ArrayList { child };

        var use = new ClassDict("renpy.sl2.slast", "SLUse");
        use["target"] = "game_menu";
        use["block"] = block;
        // Kein args → nackter Target-Name

        var containerBlock = new ClassDict("renpy.sl2.slast", "SLBlock");
        containerBlock["keyword"] = new ArrayList();
        containerBlock["children"] = new ArrayList { use };
        var slScreen = new ClassDict("renpy.sl2.slast", "SLScreen");
        slScreen["name"] = "file_slots";
        slScreen["keyword"] = new ArrayList();
        slScreen["children"] = new ArrayList { containerBlock };

        var astScreen = Node("renpy.ast.Screen", ("screen", slScreen));
        var text = _dec.Decompile(new object[] { astScreen });
        text.Should().Contain("use game_menu:");
        text.Should().Contain("text \"Save-Grid\"");
        text.Should().NotContain("SLUse mit Block");
    }

    [Fact]
    public void Translate_block_emits_translate_lang_identifier()
    {
        var translate = Node("renpy.ast.Translate",
            ("language", "de"),
            ("identifier", "greeting_abc123"),
            ("block", new ArrayList { Node("renpy.ast.Say", ("what", "Hallo!")) }));
        var endMarker = Node("renpy.ast.EndTranslate");
        var text = _dec.Decompile(new object[] { translate, endMarker });
        text.Should().Contain("translate de greeting_abc123:");
        text.Should().Contain("    \"Hallo!\"");
        // EndTranslate ist ein Compiler-Marker — im Output darf nichts davon stehen.
        text.Should().NotContain("EndTranslate");
    }

    [Fact]
    public void Translate_strings_are_grouped_into_single_block_per_language()
    {
        var ts1 = Node("renpy.ast.TranslateString", ("language", "de"), ("old", "Hello"), ("new", "Hallo"));
        var ts2 = Node("renpy.ast.TranslateString", ("language", "de"), ("old", "Bye"),   ("new", "Tschüss"));
        var ts3 = Node("renpy.ast.TranslateString", ("language", "de"), ("old", "Yes"),   ("new", "Ja"));
        var text = _dec.Decompile(new object[] { ts1, ts2, ts3 });

        // Genau EIN "translate de strings:"-Header für die drei Nodes
        var headerCount = System.Text.RegularExpressions.Regex.Matches(
            text, @"^translate de strings:", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        headerCount.Should().Be(1, "aufeinanderfolgende TranslateStrings gehören in EINEN Block");

        text.Should().Contain("old \"Hello\"");
        text.Should().Contain("new \"Hallo\"");
        text.Should().Contain("old \"Bye\"");
        text.Should().Contain("new \"Tschüss\"");
        text.Should().Contain("old \"Yes\"");
        text.Should().Contain("new \"Ja\"");
    }

    [Fact]
    public void Translate_strings_of_different_languages_get_separate_blocks()
    {
        var de = Node("renpy.ast.TranslateString", ("language", "de"), ("old", "Hi"), ("new", "Hallo"));
        var en = Node("renpy.ast.TranslateString", ("language", "en"), ("old", "Hi"), ("new", "Hi"));
        var text = _dec.Decompile(new object[] { de, en });
        text.Should().Contain("translate de strings:");
        text.Should().Contain("translate en strings:");
    }

    [Fact]
    public void Screen_parameters_are_emitted_from_signature_dict()
    {
        // Ren'Py speichert Screen-Parameter als Signature.parameters =
        // OrderedDict {name: Parameter}. Der User schrieb
        // "screen file_slots(title):", der Compiler machte {"title": Parameter}
        // draus. Wenn wir das ignorieren, verliert der Screen seinen Parameter
        // und "use file_slots(_(\"Load\"))" kracht mit "does not take positional
        // arguments".
        var params_ = new Hashtable
        {
            ["title"] = new ClassDict("renpy.parameter", "Parameter"),
        };
        var sig = new ClassDict("renpy.parameter", "Signature");
        sig["parameters"] = params_;

        var slScreen = new ClassDict("renpy.sl2.slast", "SLScreen");
        slScreen["name"] = "file_slots";
        slScreen["parameters"] = sig;
        slScreen["keyword"] = new ArrayList();
        slScreen["children"] = new ArrayList();

        var astScreen = Node("renpy.ast.Screen", ("screen", slScreen));
        var text = _dec.Decompile(new object[] { astScreen });
        text.Should().Contain("screen file_slots(title):");
    }

    [Fact]
    public void Screen_parameters_from_hashtable_fallback_sorted_no_default_first()
    {
        // Bei Legacy-Hashtable (kein OrderedDict-Constructor) ist die Order weg.
        // Wir sortieren als Fallback: no-default zuerst, damit Ren'Py's Regel
        // "non-default parameter follows a default parameter" nicht auslöst.
        var noDefault = new ClassDict("renpy.parameter", "Parameter");
        noDefault["kind"] = 1; // POSITIONAL_OR_KEYWORD
        var withDefault = new ClassDict("renpy.parameter", "Parameter");
        withDefault["kind"] = 1;
        withDefault["default"] = "0.5";

        // Hashtable garantiert KEINE Insertion-Order — hier absichtlich
        // "verkehrte" Reihenfolge einfügen, damit unser Sort greifen muss.
        var params_ = new Hashtable
        {
            ["y"] = withDefault,     // hat default
            ["x"] = withDefault,     // hat default
            ["chat"] = noDefault,    // KEIN default — muss vorne stehen
        };
        var sig = new ClassDict("renpy.parameter", "Signature");
        sig["parameters"] = params_;

        var slScreen = new ClassDict("renpy.sl2.slast", "SLScreen");
        slScreen["name"] = "phone_chat";
        slScreen["parameters"] = sig;
        slScreen["keyword"] = new ArrayList();
        slScreen["children"] = new ArrayList();

        var astScreen = Node("renpy.ast.Screen", ("screen", slScreen));
        var text = _dec.Decompile(new object[] { astScreen });

        // "chat" (no default) muss VOR "x=" oder "y=" stehen.
        var line = text.Split('\n').First(l => l.Contains("screen phone_chat"));
        int chatIdx = line.IndexOf("chat");
        int xIdx = line.IndexOf("x=");
        int yIdx = line.IndexOf("y=");
        chatIdx.Should().BeLessThan(xIdx, "no-default 'chat' muss vor 'x=' stehen");
        chatIdx.Should().BeLessThan(yIdx, "no-default 'chat' muss vor 'y=' stehen");
    }

    [Fact]
    public void Screen_var_args_and_var_kwargs_get_star_prefixes()
    {
        var argsParam = new ClassDict("renpy.parameter", "Parameter");
        argsParam["kind"] = "Parameter.VAR_POSITIONAL";
        var kwargsParam = new ClassDict("renpy.parameter", "Parameter");
        kwargsParam["kind"] = "Parameter.VAR_KEYWORD";
        var titleParam = new ClassDict("renpy.parameter", "Parameter");
        titleParam["default"] = "\"Default\"";

        var params_ = new Hashtable
        {
            ["title"] = titleParam,
            ["args"] = argsParam,
            ["kwargs"] = kwargsParam,
        };
        var sig = new ClassDict("renpy.parameter", "Signature");
        sig["parameters"] = params_;

        var slScreen = new ClassDict("renpy.sl2.slast", "SLScreen");
        slScreen["name"] = "flex";
        slScreen["parameters"] = sig;
        slScreen["keyword"] = new ArrayList();
        slScreen["children"] = new ArrayList();

        var astScreen = Node("renpy.ast.Screen", ("screen", slScreen));
        var text = _dec.Decompile(new object[] { astScreen });
        text.Should().Contain("title=\"Default\"");
        text.Should().Contain("*args");
        text.Should().Contain("**kwargs");
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
        // Widgets ohne Kinder werden inline emittiert (siehe SLDisplayable-Regel),
        // damit Ren'Py "at TRANSFORM(child)" nicht als ATL-Block missinterpretiert.
        text.Should().Contain("textbutton \"Start\" action Start()");
        text.Should().Contain("if condition:");
    }

    [Fact]
    public void Empty_style_declaration_has_no_colon_and_no_pass()
    {
        // Ren'Py kennt "pass" NICHT als Style-Property ("style property pass
        // is not known"). Ein leerer Style darf ohne Doppelpunkt geschrieben
        // werden — das ist die richtige Ausgabe.
        var style = Node("renpy.ast.Style",
            ("style_name", "window"),
            ("parent", "default"),
            ("properties", new Hashtable()));
        var text = _dec.Decompile(new object[] { style });
        text.Should().Contain("style window is default\n");
        text.Should().NotContain("style window is default:");
        text.Should().NotContain("    pass");
    }

    [Fact]
    public void Screen_zorder_only_emitted_once_when_present_in_both_keyword_and_node_field()
    {
        // Der Ren'Py-Compiler kann zorder sowohl im SLScreen.zorder-Feld als
        // auch im keyword-Feld ablegen. Wir wollen NICHT beides emittieren —
        // sonst wirft der Parser "keyword argument 'zorder' appears more than
        // once in a screen statement".
        var slScreen = new ClassDict("renpy.sl2.slast", "SLScreen");
        slScreen["name"] = "say";
        slScreen["zorder"] = "25";
        slScreen["keyword"] = new ArrayList { new object[] { "zorder", "25" } };
        slScreen["children"] = new ArrayList();
        var astScreen = Node("renpy.ast.Screen", ("screen", slScreen));
        var text = _dec.Decompile(new object[] { astScreen });
        var zorderCount = System.Text.RegularExpressions.Regex.Matches(text, @"^\s+zorder\s",
            System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        zorderCount.Should().Be(1, "zorder darf nur einmal ausgegeben werden");
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
    public void Transform_gets_args_kwargs_fallback_when_called_with_arg_in_at_clause()
    {
        // Ren'Py speichert Transform-Parameter nicht in der rpyc. Wenn ein
        // Screen "at TRANSFORM(arg)" ruft und wir keine Parameter emittieren,
        // wird das Argument als Child-Displayable interpretiert
        // → "Not a displayable: 0". Heuristischer Fallback: bei gefundenem
        // Aufruf ergänzen wir (*args, **kwargs), damit Ren'Py das Argument
        // als Parameter bindet statt als Child.
        var atlBlock = new ClassDict("renpy.atl", "RawBlock");
        atlBlock["statements"] = new ArrayList();
        var transform = Node("renpy.ast.Transform", ("varname", "myTransform"), ("atl", atlBlock));

        // Ein SLDisplayable, das den Transform mit einem Argument aufruft.
        var addNode = new ClassDict("renpy.sl2.slast", "SLDisplayable");
        addNode["name"] = "add"; addNode["positional"] = new ArrayList { "Text(\"x\")" };
        addNode["keyword"] = new ArrayList
        {
            new object[] { "at", "myTransform(msg[\"slot_index\"])" },
        };
        addNode["children"] = new ArrayList();
        var block = new ClassDict("renpy.sl2.slast", "SLBlock");
        block["keyword"] = new ArrayList(); block["children"] = new ArrayList { addNode };
        var slScreen = new ClassDict("renpy.sl2.slast", "SLScreen");
        slScreen["name"] = "s"; slScreen["keyword"] = new ArrayList();
        slScreen["children"] = new ArrayList { block };
        var astScreen = Node("renpy.ast.Screen", ("screen", slScreen));

        var text = _dec.Decompile(new object[] { transform, astScreen });
        // Ren'Py verbietet *args/**kwargs in transform-Statements
        // ("the transform statement does not take *args"). Also nur benannte
        // Parameter mit Defaults — genau so viele wie der Aufruf braucht.
        text.Should().Contain("transform myTransform(_arg0=None):");
        text.Should().NotContain("*args");
    }

    [Fact]
    public void Transform_gets_multiple_arg_fallbacks_when_call_has_multiple_args()
    {
        // Aufruf mit 2 Argumenten inkl. verschachteltem Ausdruck (der die
        // Argument-Zählung nicht durcheinanderbringen darf).
        var atlBlock = new ClassDict("renpy.atl", "RawBlock");
        atlBlock["statements"] = new ArrayList();
        var transform = Node("renpy.ast.Transform", ("varname", "twoArg"), ("atl", atlBlock));

        var addNode = new ClassDict("renpy.sl2.slast", "SLDisplayable");
        addNode["name"] = "add"; addNode["positional"] = new ArrayList { "Text(\"x\")" };
        addNode["keyword"] = new ArrayList
        {
            new object[] { "at", "twoArg(msg[\"a\"], [1, 2, 3])" }, // 2 args: dict-access + list
        };
        addNode["children"] = new ArrayList();
        var block = new ClassDict("renpy.sl2.slast", "SLBlock");
        block["keyword"] = new ArrayList(); block["children"] = new ArrayList { addNode };
        var slScreen = new ClassDict("renpy.sl2.slast", "SLScreen");
        slScreen["name"] = "s"; slScreen["keyword"] = new ArrayList();
        slScreen["children"] = new ArrayList { block };
        var astScreen = Node("renpy.ast.Screen", ("screen", slScreen));

        var text = _dec.Decompile(new object[] { transform, astScreen });
        text.Should().Contain("transform twoArg(_arg0=None, _arg1=None):");
    }

    [Fact]
    public void Transform_without_argument_call_stays_bare()
    {
        var atlBlock = new ClassDict("renpy.atl", "RawBlock");
        atlBlock["statements"] = new ArrayList();
        var transform = Node("renpy.ast.Transform", ("varname", "unused"), ("atl", atlBlock));
        var text = _dec.Decompile(new object[] { transform });
        text.Should().Contain("transform unused:");
        text.Should().NotContain("unused(*args");
    }

    [Fact]
    public void Transform_extracts_parameter_name_from_atl_body_pyexpr()
    {
        // Original: transform delayed_blink(delay):
        //              pause delay
        // Ren'Py speichert den Parameternamen "delay" nicht im rpyc. Wenn
        // wir nur "_arg0=None" emittieren, referenziert der ATL-Body noch
        // immer "delay" → NameError: name 'delay' is not defined.
        // Fix: freie Identifier aus PyExpr-Bodys ausschlachten und als
        // Parameter-Namen ausgeben — wenn ein Aufrufer Argumente übergibt.
        var delayExpr = new ClassDict("renpy.astsupport", "PyExpr");
        delayExpr["__args__"] = new object[] { "delay" };
        var pauseStmt = new ClassDict("renpy.atl", "RawMultipurpose");
        pauseStmt["warper"] = "pause"; pauseStmt["duration"] = delayExpr;
        pauseStmt["expressions"] = new ArrayList(); pauseStmt["properties"] = new ArrayList();
        var block = new ClassDict("renpy.atl", "RawBlock");
        block["statements"] = new ArrayList { pauseStmt };
        var tr = Node("renpy.ast.Transform", ("varname", "delayed_blink"), ("atl", block));

        // Aufrufer mit 1 Argument — braucht Parameter.
        var addNode = new ClassDict("renpy.sl2.slast", "SLDisplayable");
        addNode["name"] = "add"; addNode["positional"] = new ArrayList { "Text(\"x\")" };
        addNode["keyword"] = new ArrayList { new object[] { "at", "delayed_blink(3)" } };
        addNode["children"] = new ArrayList();
        var slBlock = new ClassDict("renpy.sl2.slast", "SLBlock");
        slBlock["keyword"] = new ArrayList(); slBlock["children"] = new ArrayList { addNode };
        var slScreen = new ClassDict("renpy.sl2.slast", "SLScreen");
        slScreen["name"] = "s"; slScreen["keyword"] = new ArrayList();
        slScreen["children"] = new ArrayList { slBlock };
        var astScreen = Node("renpy.ast.Screen", ("screen", slScreen));

        var text = _dec.Decompile(new object[] { tr, astScreen });
        text.Should().Contain("transform delayed_blink(delay=None):");
        text.Should().Contain("    pause delay");
        text.Should().NotContain("_arg0");
    }

    [Fact]
    public void Transform_without_call_args_stays_parameterless_even_with_body_refs()
    {
        // Regression: credits_scroll_transform aus Boundaries of Morality
        // hatte im Original KEINEN Parameter — "scroll_duration" im Body war
        // eine Store-Variable ($ scroll_duration = 200). Wenn wir fälschlich
        // einen Parameter "scroll_duration=None" hinzufügen, wird die
        // Store-Var geshadowed und Ren'Py wirft
        // TypeError: '>=' not supported between 'float' and 'NoneType'
        // beim ATL-Rendering.
        var sdExpr = new ClassDict("renpy.astsupport", "PyExpr");
        sdExpr["__args__"] = new object[] { "scroll_duration" };
        var stmt = new ClassDict("renpy.atl", "RawMultipurpose");
        stmt["warper"] = "linear"; stmt["duration"] = sdExpr;
        stmt["expressions"] = new ArrayList();
        stmt["properties"] = new ArrayList { new object[] { "yoffset", "-12000" } };
        var block = new ClassDict("renpy.atl", "RawBlock");
        block["statements"] = new ArrayList { stmt };
        var tr = Node("renpy.ast.Transform",
            ("varname", "credits_scroll_transform"), ("atl", block));

        // Aufrufer ruft OHNE Argumente ("at credits_scroll_transform").
        var addNode = new ClassDict("renpy.sl2.slast", "SLDisplayable");
        addNode["name"] = "frame"; addNode["positional"] = new ArrayList();
        addNode["keyword"] = new ArrayList { new object[] { "at", "credits_scroll_transform" } };
        addNode["children"] = new ArrayList();
        var slBlock = new ClassDict("renpy.sl2.slast", "SLBlock");
        slBlock["keyword"] = new ArrayList(); slBlock["children"] = new ArrayList { addNode };
        var slScreen = new ClassDict("renpy.sl2.slast", "SLScreen");
        slScreen["name"] = "credits"; slScreen["keyword"] = new ArrayList();
        slScreen["children"] = new ArrayList { slBlock };
        var astScreen = Node("renpy.ast.Screen", ("screen", slScreen));

        var text = _dec.Decompile(new object[] { tr, astScreen });
        text.Should().Contain("transform credits_scroll_transform:");
        text.Should().NotContain("scroll_duration=None");
    }

    [Fact]
    public void Transform_merges_call_arg_count_and_extracted_names()
    {
        // Body verwendet nur "a", aber Aufrufer übergibt 3 Argumente.
        // Erwartung: 1× extrahierter Name + 2× _argN-Fallback.
        var expr = new ClassDict("renpy.astsupport", "PyExpr");
        expr["__args__"] = new object[] { "a" };
        var stmt = new ClassDict("renpy.atl", "RawMultipurpose");
        stmt["warper"] = "linear"; stmt["duration"] = expr;
        stmt["expressions"] = new ArrayList(); stmt["properties"] = new ArrayList();
        var block = new ClassDict("renpy.atl", "RawBlock");
        block["statements"] = new ArrayList { stmt };
        var transform = Node("renpy.ast.Transform", ("varname", "mixed"), ("atl", block));

        var addNode = new ClassDict("renpy.sl2.slast", "SLDisplayable");
        addNode["name"] = "add"; addNode["positional"] = new ArrayList { "Text(\"x\")" };
        addNode["keyword"] = new ArrayList { new object[] { "at", "mixed(1, 2, 3)" } };
        addNode["children"] = new ArrayList();
        var slBlock = new ClassDict("renpy.sl2.slast", "SLBlock");
        slBlock["keyword"] = new ArrayList(); slBlock["children"] = new ArrayList { addNode };
        var slScreen = new ClassDict("renpy.sl2.slast", "SLScreen");
        slScreen["name"] = "s"; slScreen["keyword"] = new ArrayList();
        slScreen["children"] = new ArrayList { slBlock };
        var astScreen = Node("renpy.ast.Screen", ("screen", slScreen));

        var text = _dec.Decompile(new object[] { transform, astScreen });
        text.Should().Contain("transform mixed(a=None, _arg1=None, _arg2=None):");
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
        // Prio 0 wird weggelassen — `init 0:` schreibt niemand in .rpy.
        text.Should().Contain("init:");
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
    public void Call_followed_by_auto_sync_label_emits_from_clause_explicitly()
    {
        // Ren'Py-Compiler fügt nach `call X` ein `Label(_call_X)` + `Pass`
        // ein. Wir emittieren das ab v0.12.9 IMMER als `from _call_X` —
        // auch fuer den ersten Call — weil Ren'Py 8.5+ sonst beim
        // Re-Kompilieren einen automatischen `_call_X_1` generiert, der
        // mit spaeteren explizit-benannten `_call_X_N` kollidiert
        // (Interview Desires 0.23, v0.12.8-Duplicate-Label-Bug).
        var script = new object[]
        {
            Node("renpy.ast.Call", ("label", "target")),
            Node("renpy.ast.Label", ("_name", "_call_target")),
            Node("renpy.ast.Pass"),
            Node("renpy.ast.Say", ("what", "danach")),
        };
        var text = _dec.Decompile(script);
        text.Should().Contain("call target from _call_target");
        text.Should().Contain("\"danach\"");
        // Das Label-Statement selbst darf nicht als Zeile emittiert werden —
        // nur als from-Suffix am Call.
        text.Split('\n').Should().NotContain(l => l.Trim() == "label _call_target:");
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
    public void Call_with_positional_arguments_emits_arg_tuple()
    {
        // Regression-Test fuer v0.8.4-Bug (Sophia Parker 0.230):
        // `call unlock("day20_...") from _call_unlock_55` wurde als
        // `call unlock from _call_unlock_55` emittiert → NameError im Spiel,
        // weil das `label unlock(label_name)` keinen Parameter bekam.
        var argInfo = new ClassDict("renpy.ast", "ArgumentInfo");
        argInfo["arguments"] = new object[]
        {
            new object[] { null!, "\"scene_a\"" }, // positional
        };
        var script = new object[]
        {
            Node("renpy.ast.Call", ("label", "unlock"), ("arguments", argInfo)),
            Node("renpy.ast.Label", ("_name", "_call_unlock_1")),
            Node("renpy.ast.Pass"),
        };
        var text = _dec.Decompile(script);
        text.Should().Contain("call unlock (\"scene_a\") from _call_unlock_1");
    }

    [Fact]
    public void Call_with_keyword_arguments_emits_kwargs()
    {
        var argInfo = new ClassDict("renpy.ast", "ArgumentInfo");
        argInfo["arguments"] = new object[]
        {
            new object[] { "who", "\"Sophia\"" },
            new object[] { "mood", "\"happy\"" },
        };
        var script = new object[]
        {
            Node("renpy.ast.Call", ("label", "greet"), ("arguments", argInfo)),
        };
        var text = _dec.Decompile(script);
        text.Should().Contain("call greet (who=\"Sophia\", mood=\"happy\")");
    }

    [Fact]
    public void Label_with_parameters_emits_parameter_list()
    {
        // `label unlock(label_name):` — sonst wuerde beim Aufruf mit
        // Argument der Wert nicht gebunden werden (siehe Sophia Parker 0.230).
        var paramInfo = new ClassDict("renpy.ast", "ParameterInfo");
        paramInfo["parameters"] = new object[]
        {
            new object[] { "label_name", null! },
            new object[] { "extra", "42" },
        };
        var script = new object[]
        {
            Node("renpy.ast.Label", ("_name", "unlock"), ("parameters", paramInfo)),
        };
        var text = _dec.Decompile(script);
        text.Should().Contain("label unlock(label_name, extra=42):");
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

    // ---- v0.8.6-Regressions (Diff gegen unrpyc) ---------------------------

    [Fact]
    public void Scene_followed_by_with_transition_merges_into_one_line()
    {
        // `scene X\nwith fade` → `scene X with fade` (elegantes unrpyc-Format).
        var imspec = new object?[]
        {
            new object[] { "day20_waking_up", "1" }, null, null, null, "master", null, null,
        };
        var script = new object[]
        {
            Node("renpy.ast.Scene", ("imspec", imspec)),
            Node("renpy.ast.With", ("expr", "fade")),
        };
        var text = _dec.Decompile(script);
        text.Should().Contain("scene day20_waking_up 1 with fade");
        // Keine separate `with fade`-Zeile mehr.
        text.Split('\n').Should().NotContain(l => l.Trim() == "with fade");
    }

    [Fact]
    public void Show_followed_by_with_None_does_not_merge()
    {
        // `with None` ist ein Compiler-Artefakt (killt implizite Fade-in) —
        // NICHT in die Scene-Zeile mergen, sondern separat lassen.
        var imspec = new object?[]
        {
            new object[] { "hero" }, null, null, null, "master", null, null,
        };
        var script = new object[]
        {
            Node("renpy.ast.Show", ("imspec", imspec)),
            Node("renpy.ast.With", ("expr", "None")),
        };
        var text = _dec.Decompile(script);
        text.Should().Contain("show hero");
        text.Should().NotContain("show hero with None");
    }

    [Fact]
    public void Init_with_single_python_uses_modern_compact_form()
    {
        // `init -998:\n  python:\n    x = 1` → `init -998 python:\n    x = 1`.
        var pyCode = new ClassDict("renpy.ast", "PyCode");
        pyCode["__args__"] = new object[] { "active_parts.append(2)" };
        var python = Node("renpy.ast.Python", ("code", pyCode));
        var init = Node("renpy.ast.Init",
            ("priority", -998),
            ("block", new ArrayList { python }));
        var text = _dec.Decompile(new object[] { init });
        text.Should().Contain("init -998 python:");
        text.Should().Contain("    active_parts.append(2)");
        // Alte doppelt-genestete Form darf nicht mehr da sein.
        text.Should().NotContain("init -998:\n    python:");
    }

    [Fact]
    public void Label_followed_by_menu_becomes_named_menu()
    {
        // `label day21_X: <empty>` + `menu:` → `menu day21_X:`.
        var menu = Node("renpy.ast.Menu",
            ("items", new ArrayList
            {
                new object?[] { "Choice A", "True", new ArrayList() },
            }));
        var label = Node("renpy.ast.Label",
            ("_name", "day21_Sophia_decides"),
            ("block", new ArrayList()));
        var text = _dec.Decompile(new object[] { label, menu });
        text.Should().Contain("menu day21_Sophia_decides:");
        text.Should().NotContain("label day21_Sophia_decides:");
    }

    [Fact]
    public void Menu_with_item_arguments_emits_kwarg_tuple_on_choices()
    {
        // Menu-Items koennen Attribute wie `(wt={…}, disabled={…})` haben —
        // in Ren'Py 8.x als parallele item_arguments-Liste am Menu-Node.
        var argInfo = new ClassDict("renpy.ast", "ArgumentInfo");
        argInfo["arguments"] = new object[]
        {
            new object[] { "wt", "{\"filthy\": 1}" },
        };
        var menu = Node("renpy.ast.Menu",
            ("items", new ArrayList
            {
                new object?[] { "Indulge in the fun", "True", new ArrayList() },
            }),
            ("item_arguments", new ArrayList { argInfo }));
        var text = _dec.Decompile(new object[] { menu });
        text.Should().Contain("\"Indulge in the fun\"(wt={\"filthy\": 1}):");
    }

    [Fact]
    public void Menu_with_own_arguments_emits_menu_args()
    {
        // `menu name(box_yalign=0.5):` — Attribute am Menu-Level (nicht per Item).
        var argInfo = new ClassDict("renpy.ast", "ArgumentInfo");
        argInfo["arguments"] = new object[]
        {
            new object[] { "box_yalign", "0.5" },
        };
        var menu = Node("renpy.ast.Menu",
            ("items", new ArrayList
            {
                new object?[] { "OK", "True", new ArrayList() },
            }),
            ("arguments", argInfo));
        var label = Node("renpy.ast.Label",
            ("_name", "start_choice"),
            ("block", new ArrayList()));
        var text = _dec.Decompile(new object[] { label, menu });
        text.Should().Contain("menu start_choice(box_yalign=0.5):");
    }
}
