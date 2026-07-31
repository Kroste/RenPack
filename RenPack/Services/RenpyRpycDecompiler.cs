using System.Collections;
using System.Globalization;
using System.Text;
using NLog;
using Razorvine.Pickle.Objects;

namespace RenPack.Services;

/// <summary>
/// Wandelt die AST-Statement-Liste aus <see cref="RenpyRpycService"/> zurück in
/// Ren'Py-Skript-Quelltext. **Basic-Decompiler** — deckt die häufigsten
/// Node-Typen ab (Label/Say/Menu/Jump/Call/Return/If/Show/Scene/Hide/With/
/// Python/Init/Pass/Define/Default/UserStatement), unbekannte Nodes werden als
/// Kommentar (<c># &lt;unknown: mod.Class&gt;</c>) mit ausgegeben, damit der
/// Zeilenversatz erhalten bleibt.
///
/// Für vollen Feature-Parity (Screens, ATL, Transforms, Style-Definitions) ist
/// weiterhin unrpyc das Tool der Wahl — die App liefert hier eine cross-
/// platform-Basisvariante ohne Python-Dependency.
/// </summary>
public sealed class RenpyRpycDecompiler
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private const string Indent = "    ";

    public string Decompile(IReadOnlyList<object?> statements)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Decompiled by RenPack — Basic-Decompiler ohne vollständige Ren'Py-Sprachdeckung.");
        sb.AppendLine("# Für Screens, ATL und komplexe Init-Blöcke bitte weiterhin unrpyc verwenden.");
        sb.AppendLine();
        EmitBlock(sb, statements, indent: 0);
        return sb.ToString();
    }

    // ---- Block-Emit --------------------------------------------------------

    private void EmitBlock(StringBuilder sb, IEnumerable statements, int indent)
    {
        foreach (var stmt in statements)
        {
            if (stmt is ClassDict node)
                EmitNode(sb, node, indent);
            else if (stmt is not null)
                AppendIndented(sb, indent, $"# <unbekannt: {stmt.GetType().Name}>");
        }
    }

    private void EmitNode(StringBuilder sb, ClassDict node, int indent)
    {
        switch (node.ClassName)
        {
            case "renpy.ast.Label": EmitLabel(sb, node, indent); break;
            case "renpy.ast.Say": EmitSay(sb, node, indent); break;
            case "renpy.ast.Menu": EmitMenu(sb, node, indent); break;
            case "renpy.ast.If": EmitIf(sb, node, indent); break;
            case "renpy.ast.Jump": EmitJump(sb, node, indent); break;
            case "renpy.ast.Call": EmitCall(sb, node, indent); break;
            case "renpy.ast.Return": EmitReturn(sb, node, indent); break;
            case "renpy.ast.Show": EmitShowHideScene(sb, node, indent, "show"); break;
            case "renpy.ast.Scene": EmitShowHideScene(sb, node, indent, "scene"); break;
            case "renpy.ast.Hide": EmitShowHideScene(sb, node, indent, "hide"); break;
            case "renpy.ast.With": EmitWith(sb, node, indent); break;
            case "renpy.ast.Python": EmitPython(sb, node, indent, prefix: ""); break;
            case "renpy.ast.EarlyPython": EmitPython(sb, node, indent, prefix: "early "); break;
            case "renpy.ast.Init": EmitInit(sb, node, indent); break;
            case "renpy.ast.Pass": AppendIndented(sb, indent, "pass"); break;
            case "renpy.ast.Define": EmitDefine(sb, node, indent, "define"); break;
            case "renpy.ast.Default": EmitDefine(sb, node, indent, "default"); break;
            case "renpy.ast.UserStatement": EmitUserStatement(sb, node, indent); break;
            default:
                AppendIndented(sb, indent, $"# <unsupported: {node.ClassName}>");
                break;
        }
    }

    // ---- Node-spezifische Writer -------------------------------------------

    private void EmitLabel(StringBuilder sb, ClassDict node, int indent)
    {
        string name = AsString(node.GetValueOrDefault("name") ?? node.GetValueOrDefault("_name"));
        AppendIndented(sb, indent, $"label {name}:");
        if (node.GetValueOrDefault("block") is IEnumerable block)
            EmitBlock(sb, block, indent + 1);
    }

    private static void EmitSay(StringBuilder sb, ClassDict node, int indent)
    {
        string what = AsString(node.GetValueOrDefault("what"));
        object? who = node.GetValueOrDefault("who");
        string whoText = who is null ? "" : AsString(who);
        string line = string.IsNullOrEmpty(whoText)
            ? $"\"{EscapeString(what)}\""
            : $"{whoText} \"{EscapeString(what)}\"";
        AppendIndented(sb, indent, line);
    }

    private void EmitMenu(StringBuilder sb, ClassDict node, int indent)
    {
        AppendIndented(sb, indent, "menu:");
        if (node.GetValueOrDefault("items") is not IEnumerable items) return;
        foreach (var it in items)
        {
            // Menu-Item ist ein Tupel (caption, condition, block)
            if (it is not object[] arr || arr.Length < 3) continue;
            string caption = AsString(arr[0]);
            string condition = AsString(arr[1]);
            string suffix = condition is "True" or "" ? "" : $" if {condition}";
            AppendIndented(sb, indent + 1, $"\"{EscapeString(caption)}\"{suffix}:");
            if (arr[2] is IEnumerable block) EmitBlock(sb, block, indent + 2);
            else AppendIndented(sb, indent + 2, "pass");
        }
    }

    private void EmitIf(StringBuilder sb, ClassDict node, int indent)
    {
        if (node.GetValueOrDefault("entries") is not IEnumerable entries) return;
        bool first = true;
        foreach (var e in entries)
        {
            if (e is not object[] arr || arr.Length < 2) continue;
            string cond = AsString(arr[0]);
            string head = first
                ? $"if {cond}:"
                : cond is "True" or "" ? "else:" : $"elif {cond}:";
            AppendIndented(sb, indent, head);
            if (arr[1] is IEnumerable block) EmitBlock(sb, block, indent + 1);
            else AppendIndented(sb, indent + 1, "pass");
            first = false;
        }
    }

    private static void EmitJump(StringBuilder sb, ClassDict node, int indent)
    {
        string target = AsString(node.GetValueOrDefault("target"));
        bool expr = node.GetValueOrDefault("expression") is bool b && b;
        AppendIndented(sb, indent, expr ? $"jump expression {target}" : $"jump {target}");
    }

    private static void EmitCall(StringBuilder sb, ClassDict node, int indent)
    {
        string target = AsString(node.GetValueOrDefault("label"));
        bool expr = node.GetValueOrDefault("expression") is bool b && b;
        AppendIndented(sb, indent, expr ? $"call expression {target}" : $"call {target}");
    }

    private static void EmitReturn(StringBuilder sb, ClassDict node, int indent)
    {
        var expr = node.GetValueOrDefault("expression");
        string suffix = expr is null ? "" : " " + AsString(expr);
        AppendIndented(sb, indent, $"return{suffix}");
    }

    private static void EmitShowHideScene(StringBuilder sb, ClassDict node, int indent, string keyword)
    {
        // imspec ist ein Tupel — meist (name-tuple, at-list, layer, ...). Wir nehmen
        // den Namen als Space-separierte Attribut-Kette.
        var imspec = node.GetValueOrDefault("imspec");
        string spec;
        if (imspec is object[] arr && arr.Length > 0 && arr[0] is IEnumerable nameParts)
            spec = string.Join(" ", nameParts.Cast<object?>().Select(AsString));
        else if (imspec is not null)
            spec = AsString(imspec);
        else
            spec = "";
        AppendIndented(sb, indent, $"{keyword} {spec}".TrimEnd());
    }

    private static void EmitWith(StringBuilder sb, ClassDict node, int indent)
    {
        var expr = node.GetValueOrDefault("expr") ?? node.GetValueOrDefault("expression");
        AppendIndented(sb, indent, $"with {AsString(expr)}");
    }

    private static void EmitPython(StringBuilder sb, ClassDict node, int indent, string prefix)
    {
        string code = AsString(node.GetValueOrDefault("code"));
        // Einzeiliger Code ohne Newline → $ shortcut
        if (!code.Contains('\n'))
        {
            AppendIndented(sb, indent, $"${(string.IsNullOrEmpty(prefix) ? "" : " " + prefix)} {code}".TrimStart());
            return;
        }
        AppendIndented(sb, indent, $"{prefix}python:");
        foreach (var line in code.Split('\n'))
            AppendIndented(sb, indent + 1, line);
    }

    private void EmitInit(StringBuilder sb, ClassDict node, int indent)
    {
        int priority = node.GetValueOrDefault("priority") is int p ? p : 0;
        AppendIndented(sb, indent, $"init {priority}:");
        if (node.GetValueOrDefault("block") is IEnumerable block)
            EmitBlock(sb, block, indent + 1);
    }

    private static void EmitDefine(StringBuilder sb, ClassDict node, int indent, string keyword)
    {
        string name = AsString(node.GetValueOrDefault("varname") ?? node.GetValueOrDefault("name"));
        string code = AsString(node.GetValueOrDefault("code"));
        string op = AsString(node.GetValueOrDefault("operator")); // "=" usw.
        if (string.IsNullOrEmpty(op)) op = "=";
        AppendIndented(sb, indent, $"{keyword} {name} {op} {code}".TrimEnd());
    }

    private static void EmitUserStatement(StringBuilder sb, ClassDict node, int indent)
    {
        // Beliebiges Custom-Statement (z. B. "play music", "stop music", "pause").
        // Meist steht der komplette Text im "line"-Feld.
        string line = AsString(node.GetValueOrDefault("line") ?? node.GetValueOrDefault("parsed"));
        AppendIndented(sb, indent, line);
    }

    // ---- Hilfen ------------------------------------------------------------

    private static void AppendIndented(StringBuilder sb, int indent, string content)
    {
        for (int i = 0; i < indent; i++) sb.Append(Indent);
        sb.AppendLine(content);
    }

    /// <summary>Wandelt einen AST-Wert in seine Textrepräsentation. Für die
    /// häufigen Wrapper-Klassen (<c>PyExpr</c>, <c>PyCode</c>) wird der
    /// eingebettete String-Wert extrahiert; für <c>ClassDict</c> ohne bekannten
    /// String-Content bleibt der Klassenname als Kommentar.</summary>
    private static string AsString(object? v) => v switch
    {
        null => "",
        string s => s,
        bool b => b ? "True" : "False",
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        ClassDict cd => ExtractPyExpr(cd),
        IEnumerable en => string.Join(" ", en.Cast<object?>().Select(AsString)),
        _ => v.ToString() ?? "",
    };

    /// <summary>PyExpr/PyCode sind in Ren'Py <c>str</c>-Subclasses. Beim
    /// Unpickeln (via <c>__reduce__</c>) liefert der Catch-all ein
    /// <c>ClassDict</c>:
    /// <list type="bullet">
    ///   <item><c>PyExpr</c>: <c>__args__[0]</c> ist der Text (Rest:
    ///     Filename/Zeilennummer/Hash).</item>
    ///   <item><c>PyCode</c>: hat einen 7-Element-<c>__state__</c>-Tupel,
    ///     <c>state[1]</c> ist der Source-Ausdruck (selbst ein PyExpr →
    ///     rekursiv extrahieren).</item>
    /// </list>
    /// Fallback: bekannte Feldnamen aus einem Dict-State.</summary>
    private static string ExtractPyExpr(ClassDict cd)
    {
        if (cd.TryGetValue("__args__", out var av) && av is object[] { Length: >= 1 } args
            && args[0] is string first) return first;
        if (cd.TryGetValue("__state__", out var sv) && sv is object[] state)
        {
            // PyCode: state[1] ist die Source (PyExpr oder String)
            if (state.Length >= 2)
            {
                if (state[1] is string src) return src;
                if (state[1] is ClassDict inner) return ExtractPyExpr(inner);
            }
        }
        foreach (var key in new[] { "text", "value", "expr", "code", "source" })
            if (cd.TryGetValue(key, out var v) && v is string s) return s;
        return $"<{cd.ClassName}>";
    }

    private static string EscapeString(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}

internal static class ClassDictExtensions
{
    public static object? GetValueOrDefault(this ClassDict cd, string key)
        => cd.TryGetValue(key, out var v) ? v : null;
}
