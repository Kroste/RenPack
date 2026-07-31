using System.Collections;
using System.Text;
using Razorvine.Pickle.Objects;

namespace RenPack.Services;

/// <summary>
/// Schreibt Ren'Py-Screens (Screen Language 2) aus dem dekompilierten AST
/// zurück. Screens sind Ren'Py's UI-DSL — eine eigene Sprach-Ebene innerhalb
/// von .rpy-Dateien mit Widget-Deklarationen, If/For/Python-Blöcken,
/// use-Direktiven, Screen-Parametern und Widget-Attributen:
/// <code>
/// screen name(param="default"):
///     modal True
///     zorder 100
///     window:
///         style "title"
///         vbox:
///             text "hello"
///             textbutton "click" action Return()
///             if condition:
///                 text "shown"
///             for i in range(5):
///                 text "[i]"
///             use other_screen(arg)
/// </code>
/// Deckt die häufigen <c>renpy.sl2.slast.*</c>-Node-Typen ab (Screen, Block,
/// Displayable, If, For, Use, Default, Python, Transclude, ShowIf).
/// Unbekannte SL-Klassen werden als Kommentar mit <c>pass</c> emittiert.
/// </summary>
internal static class RenpySlWriter
{
    private const string Indent = "    ";

    /// <summary>Ganze Screen-Deklaration emittieren (aus einem
    /// <c>renpy.ast.Screen</c>-Node). Der Node wrappt eine <c>SLScreen</c>-
    /// Instanz im Feld <c>screen</c>.</summary>
    public static void EmitScreen(StringBuilder sb, ClassDict astScreen, int indent)
    {
        if (astScreen.GetValueOrDefault("screen") is not ClassDict slScreen ||
            slScreen.ClassName != "renpy.sl2.slast.SLScreen")
        {
            AppendIndented(sb, indent, "# <ungültiger renpy.ast.Screen ohne SLScreen>");
            AppendIndented(sb, indent, "screen unknown:");
            AppendIndented(sb, indent + 1, "pass");
            return;
        }

        string name = AsString(slScreen.GetValueOrDefault("name"));
        string parameters = FormatParameters(slScreen.GetValueOrDefault("parameters"));
        AppendIndented(sb, indent, string.IsNullOrEmpty(parameters)
            ? $"screen {name}:"
            : $"screen {name}{parameters}:");

        int bodyEmitted = 0;

        // Screen-Attribute: welche Attribute im keyword-Feld erscheinen, tracken
        // wir, damit wir sie in EmitScreenAttributes nicht doppelt ausgeben.
        var emittedKeys = new HashSet<string>(StringComparer.Ordinal);
        bodyEmitted += EmitKeywords(sb, slScreen.GetValueOrDefault("keyword"), indent + 1, emittedKeys);
        bodyEmitted += EmitScreenAttributes(sb, slScreen, indent + 1, emittedKeys);

        // Kinder-Widgets
        if (slScreen.GetValueOrDefault("children") is IEnumerable children)
            bodyEmitted += EmitChildren(sb, children, indent + 1);

        if (bodyEmitted == 0) AppendIndented(sb, indent + 1, "pass");
    }

    private static int EmitScreenAttributes(StringBuilder sb, ClassDict slScreen, int indent,
        HashSet<string>? alreadyEmitted = null)
    {
        int count = 0;
        // Nur nicht-Default-Werte emittieren (Default: layer='screens',
        // modal=False, sensitive=True, alles andere None). Und niemals doppelt,
        // wenn EmitKeywords das Attribut schon aus dem keyword-Feld ausgegeben hat.
        foreach (var (field, defaultVal) in new[]
        {
            ("tag", (string?)null), ("modal", "False"), ("zorder", null),
            ("variant", null), ("layer", "'screens'"), ("predict", null),
            ("sensitive", "True"), ("docstring", null),
        })
        {
            if (alreadyEmitted is not null && alreadyEmitted.Contains(field)) continue;
            var val = slScreen.GetValueOrDefault(field);
            if (val is null) continue;
            string s = AsAtl(val);
            if (s == "None" || s == "" || s == defaultVal) continue;
            AppendIndented(sb, indent, $"{field} {s}");
            count++;
        }
        return count;
    }

    /// <summary>Emittiert eine Kinderliste eines Blocks/Widgets. Rückgabe: Zahl
    /// der emittierten "echten" Statements (Kommentare zählen nicht — der
    /// Aufrufer nutzt das, um bei 0 ein <c>pass</c> einzufügen).</summary>
    public static int EmitChildren(StringBuilder sb, IEnumerable children, int indent)
    {
        int emitted = 0;
        foreach (var c in children)
            if (EmitSlNode(sb, c, indent)) emitted++;
        return emitted;
    }

    private static bool EmitSlNode(StringBuilder sb, object? node, int indent)
    {
        if (node is not ClassDict cd)
        {
            if (node is not null) AppendIndented(sb, indent, $"# <SL-nicht-ClassDict: {node.GetType().Name}>");
            return false;
        }
        switch (cd.ClassName)
        {
            case "renpy.sl2.slast.SLDisplayable":
                EmitDisplayable(sb, cd, indent);
                return true;
            case "renpy.sl2.slast.SLBlock":
                EmitBlock(sb, cd, indent);
                return true;
            case "renpy.sl2.slast.SLIf":
                EmitIf(sb, cd, indent);
                return true;
            case "renpy.sl2.slast.SLShowIf":
                EmitShowIf(sb, cd, indent);
                return true;
            case "renpy.sl2.slast.SLFor":
                EmitFor(sb, cd, indent);
                return true;
            case "renpy.sl2.slast.SLUse":
                EmitUse(sb, cd, indent);
                return true;
            case "renpy.sl2.slast.SLDefault":
                EmitDefault(sb, cd, indent);
                return true;
            case "renpy.sl2.slast.SLPython":
                EmitPython(sb, cd, indent);
                return true;
            case "renpy.sl2.slast.SLTransclude":
                AppendIndented(sb, indent, "transclude");
                return true;
            case "renpy.sl2.slast.SLPass":
                AppendIndented(sb, indent, "pass");
                return true;
            default:
                AppendIndented(sb, indent, $"# <unsupported SL: {cd.ClassName}>");
                AppendIndented(sb, indent, "pass");
                return true;
        }
    }

    /// <summary>Ein Displayable ist ein UI-Widget wie <c>text</c>, <c>vbox</c>,
    /// <c>textbutton</c>, <c>frame</c>. Ausgabe:
    /// <c>&lt;name&gt; &lt;pos-arg1&gt; …:</c> gefolgt vom Body (Keywords +
    /// Kinder). Bei ganz kleinen Widgets ohne Body: kompakte Ein-Zeilen-Form.</summary>
    private static void EmitDisplayable(StringBuilder sb, ClassDict cd, int indent)
    {
        string name = AsString(cd.GetValueOrDefault("name"));
        var positional = cd.GetValueOrDefault("positional") as IEnumerable;
        var keyword = cd.GetValueOrDefault("keyword") as IEnumerable;
        var children = cd.GetValueOrDefault("children") as IEnumerable;

        string posArgs = "";
        if (positional is not null)
        {
            var args = positional.Cast<object?>().Select(AsAtl).Where(s => !string.IsNullOrEmpty(s));
            posArgs = string.Join(" ", args);
        }

        // Kompaktes Widget ohne Body und ohne Kinder → eine Zeile
        bool hasChildren = children is not null && children.Cast<object?>().Any();
        bool hasKeywords = keyword is not null && keyword.Cast<object?>().Any();

        if (!hasChildren && !hasKeywords)
        {
            var line = string.IsNullOrEmpty(posArgs) ? name : $"{name} {posArgs}";
            AppendIndented(sb, indent, line);
            return;
        }

        // Kompakt-Zeile für Widgets nur mit Keywords (Ren'Py erlaubt das
        // inline), aber die klassische Form ist besser lesbar bei mehreren.
        AppendIndented(sb, indent, string.IsNullOrEmpty(posArgs)
            ? $"{name}:"
            : $"{name} {posArgs}:");
        int emitted = 0;
        if (hasKeywords) emitted += EmitKeywords(sb, keyword, indent + 1);
        if (hasChildren) emitted += EmitChildren(sb, children!, indent + 1);
        if (emitted == 0) AppendIndented(sb, indent + 1, "pass");
    }

    private static void EmitBlock(StringBuilder sb, ClassDict cd, int indent)
    {
        // Nackter SLBlock — meistens im else-Zweig von SLIf oder als Loop-Body.
        // Wir emittieren nur die Kinder + Keywords ohne eigene "block:"-Zeile,
        // weil SLBlock keine eigene Syntax hat.
        int emitted = 0;
        emitted += EmitKeywords(sb, cd.GetValueOrDefault("keyword"), indent);
        if (cd.GetValueOrDefault("children") is IEnumerable ch)
            emitted += EmitChildren(sb, ch, indent);
        if (emitted == 0) AppendIndented(sb, indent, "pass");
    }

    private static void EmitIf(StringBuilder sb, ClassDict cd, int indent)
    {
        if (cd.GetValueOrDefault("entries") is not IEnumerable entries)
        {
            AppendIndented(sb, indent, "if False:");
            AppendIndented(sb, indent + 1, "pass");
            return;
        }
        bool first = true;
        foreach (var e in entries)
        {
            if (e is not object[] arr || arr.Length < 2) continue;
            string cond = AsAtl(arr[0]);
            string head = first
                ? $"if {cond}:"
                : (string.IsNullOrEmpty(cond) || cond == "None" || cond == "True")
                    ? "else:" : $"elif {cond}:";
            AppendIndented(sb, indent, head);
            int emitted = 0;
            if (arr[1] is ClassDict block) { EmitBlock(sb, block, indent + 1); emitted = 1; }
            else if (arr[1] is IEnumerable ch) emitted = EmitChildren(sb, ch, indent + 1);
            if (emitted == 0) AppendIndented(sb, indent + 1, "pass");
            first = false;
        }
    }

    private static void EmitShowIf(StringBuilder sb, ClassDict cd, int indent)
    {
        // Wie SLIf, aber mit `showif` als Präfix.
        if (cd.GetValueOrDefault("entries") is not IEnumerable entries) return;
        bool first = true;
        foreach (var e in entries)
        {
            if (e is not object[] arr || arr.Length < 2) continue;
            string cond = AsAtl(arr[0]);
            string head = first
                ? $"showif {cond}:"
                : (string.IsNullOrEmpty(cond) || cond == "None" || cond == "True")
                    ? "else:" : $"elif {cond}:";
            AppendIndented(sb, indent, head);
            if (arr[1] is ClassDict block) EmitBlock(sb, block, indent + 1);
            else AppendIndented(sb, indent + 1, "pass");
            first = false;
        }
    }

    private static void EmitFor(StringBuilder sb, ClassDict cd, int indent)
    {
        string var = AsString(cd.GetValueOrDefault("variable"));
        string expr = AsAtl(cd.GetValueOrDefault("expression"));
        string idxExpr = AsAtl(cd.GetValueOrDefault("index_expression"));
        string head = string.IsNullOrEmpty(idxExpr)
            ? $"for {var} in {expr}:"
            : $"for {var} index {idxExpr} in {expr}:";
        AppendIndented(sb, indent, head);
        int emitted = 0;
        if (cd.GetValueOrDefault("children") is IEnumerable ch)
            emitted = EmitChildren(sb, ch, indent + 1);
        if (emitted == 0) AppendIndented(sb, indent + 1, "pass");
    }

    private static void EmitUse(StringBuilder sb, ClassDict cd, int indent)
    {
        string target = AsString(cd.GetValueOrDefault("target"));
        string args = FormatArguments(cd.GetValueOrDefault("args"));
        var id = cd.GetValueOrDefault("id");
        string line = $"use {target}{args}";
        if (id is not null && AsAtl(id) is { Length: > 0 } idStr && idStr != "None")
            line += $" id {idStr}";
        AppendIndented(sb, indent, line);
        // Optional block bei nested use (selten)
        if (cd.GetValueOrDefault("block") is ClassDict block)
        {
            // Ren'Py-Syntax für `use X:` mit block ist speziell — für v0.4c
            // erst mal als Kommentar markieren, damit der Ren'Py-Parser nicht
            // unerwartet zickt.
            AppendIndented(sb, indent + 1, "# <SLUse mit Block — teilweise unterstützt>");
            var _ = block;
        }
    }

    private static void EmitDefault(StringBuilder sb, ClassDict cd, int indent)
    {
        string var = AsString(cd.GetValueOrDefault("variable"));
        string expr = AsAtl(cd.GetValueOrDefault("expression"));
        AppendIndented(sb, indent, $"default {var} = {expr}");
    }

    private static void EmitPython(StringBuilder sb, ClassDict cd, int indent)
    {
        string code = AsAtl(cd.GetValueOrDefault("code"));
        if (!code.Contains('\n'))
        {
            AppendIndented(sb, indent, $"$ {code}");
            return;
        }
        AppendIndented(sb, indent, "python:");
        foreach (var line in code.Split('\n'))
            AppendIndented(sb, indent + 1, line);
    }

    /// <summary>Emittiert eine Keyword-Liste (aus <c>keyword</c>-Feld). Format:
    /// <c>[(name_str, expr_pyexpr), …]</c>. Rückgabe: Anzahl emittierter Zeilen.
    /// Wenn <paramref name="track"/> gesetzt, werden die emittierten Namen dort
    /// eingetragen — der Aufrufer nutzt das, um doppelte Ausgabe zu verhindern
    /// (Screen-Attribute stehen sowohl im keyword-Feld als auch als Node-Felder).</summary>
    private static int EmitKeywords(StringBuilder sb, object? keywords, int indent,
        HashSet<string>? track = null)
    {
        if (keywords is not IEnumerable en) return 0;
        int count = 0;
        foreach (var kv in en)
        {
            if (kv is not object[] arr || arr.Length < 2) continue;
            string name = AsString(arr[0]);
            string expr = AsAtl(arr[1]);
            AppendIndented(sb, indent, $"{name} {expr}");
            track?.Add(name);
            count++;
        }
        return count;
    }

    /// <summary>Formatiert Screen-Parameter (aus <c>renpy.parameter.Signature</c>
    /// oder <c>ParameterInfo</c>). Für v0.4c minimal — die vollständige Ausgabe
    /// von Ren'Py-Parameter-Syntax mit Defaults/Positional-Only/*args/**kwargs
    /// wäre eine eigene Baustelle. Wir erkennen die häufige Form: Signature mit
    /// <c>parameters</c>-Feld voller <c>Parameter</c>-Objekte.</summary>
    private static string FormatParameters(object? parameters)
    {
        if (parameters is not ClassDict cd) return "";
        // Ren'Py 8: renpy.parameter.Signature mit __args__ = (Parameters,) oder
        // ParameterInfo mit "parameters"-Feld. Wir versuchen beides.
        if (cd.GetValueOrDefault("parameters") is IEnumerable list)
        {
            var parts = new List<string>();
            foreach (var p in list)
            {
                if (p is not ClassDict pd) continue;
                string pname = AsString(pd.GetValueOrDefault("name"));
                var def = pd.GetValueOrDefault("default");
                string defStr = def is null ? "" : "=" + AsAtl(def);
                if (!string.IsNullOrEmpty(pname)) parts.Add(pname + defStr);
            }
            if (parts.Count > 0) return "(" + string.Join(", ", parts) + ")";
        }
        return "";
    }

    /// <summary>Formatiert Call-Arguments (für <c>use</c>).</summary>
    private static string FormatArguments(object? args)
    {
        if (args is not ClassDict cd) return "";
        // ArgumentInfo hat "arguments" = [(name?, value), …] und "extrapos"/"extrakw".
        if (cd.GetValueOrDefault("arguments") is IEnumerable list)
        {
            var parts = new List<string>();
            foreach (var a in list)
            {
                if (a is not object[] arr || arr.Length < 2) continue;
                string aname = arr[0] is null ? "" : AsString(arr[0]);
                string aval = AsAtl(arr[1]);
                parts.Add(string.IsNullOrEmpty(aname) ? aval : $"{aname}={aval}");
            }
            if (parts.Count > 0) return "(" + string.Join(", ", parts) + ")";
        }
        return "";
    }

    // ---- Formatierung ------------------------------------------------------

    private static string AsString(object? v) => v?.ToString() ?? "";

    private static string AsAtl(object? v) => v switch
    {
        null => "",
        string s => s,
        int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
        long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
        double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        bool b => b ? "True" : "False",
        ClassDict cd => ExtractPyExpr(cd),
        object[] arr => "(" + string.Join(", ", arr.Select(AsAtl)) + ")",
        IEnumerable en => "[" + string.Join(", ", en.Cast<object?>().Select(AsAtl)) + "]",
        _ => v.ToString() ?? "",
    };

    private static string ExtractPyExpr(ClassDict cd)
    {
        if (cd.TryGetValue("__args__", out var av) && av is object[] { Length: >= 1 } args
            && args[0] is string first) return first;
        if (cd.TryGetValue("__state__", out var sv) && sv is object[] state
            && state.Length >= 2)
        {
            if (state[1] is string src) return src;
            if (state[1] is ClassDict inner) return ExtractPyExpr(inner);
        }
        return $"<{cd.ClassName}>";
    }

    private static void AppendIndented(StringBuilder sb, int indent, string content)
    {
        for (int i = 0; i < indent; i++) sb.Append(Indent);
        sb.AppendLine(content);
    }
}
