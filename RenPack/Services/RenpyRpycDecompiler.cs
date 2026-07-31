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

    /// <summary>Emittiert einen Statement-Block mit Sonderregeln für Ren'Py-
    /// Compiler-Artefakte:
    /// <list type="bullet">
    ///   <item><b>Auto-Sync-Label nach Call</b>: der Ren'Py-Compiler fügt nach
    ///     jedem <c>call X</c> intern ein <c>Label(_call_X)</c> mit einem
    ///     <c>Pass</c>-Kind ein — die Rücksprungadresse. Im Original-.rpy steht
    ///     das nicht. Wir überspringen es beim Emittieren.</item>
    ///   <item><b>Init-Vereinfachung</b>: ein <c>Init(prio=0, [Define/Default])</c>
    ///     wird als nacktes <c>define …</c>/<c>default …</c> ohne Wrapper
    ///     ausgegeben (spart pro define einen ganzen init-Block).</item>
    /// </list></summary>
    private int EmitBlock(StringBuilder sb, IEnumerable statements, int indent)
    {
        var list = statements as IList<object?> ?? statements.Cast<object?>().ToList();
        int emitted = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var stmt = list[i];
            if (stmt is not ClassDict node)
            {
                if (stmt is not null)
                    AppendIndented(sb, indent, $"# <unbekannt: {stmt.GetType().Name}>");
                continue;
            }

            // Init(0, [einzelnes Define/Default]) → nacktes Statement
            if (node.ClassName == "renpy.ast.Init" && TryUnwrapSingletonInit(node, out var single))
            {
                EmitNode(sb, single, indent);
                emitted++;
                continue;
            }

            // Nach Call: Auto-Sync-Sequenz behandeln (siehe Kommentar oben).
            string? fromClause = null;
            if (node.ClassName == "renpy.ast.Call" && i + 1 < list.Count &&
                list[i + 1] is ClassDict maybeLabel && maybeLabel.ClassName == "renpy.ast.Label")
            {
                string labelName = AsString(maybeLabel.GetValueOrDefault("name")
                    ?? maybeLabel.GetValueOrDefault("_name"));
                string target = GetCallTarget(node);
                if (labelName == $"_call_{target}")
                {
                    i++;
                    if (i + 1 < list.Count && list[i + 1] is ClassDict p1
                        && p1.ClassName == "renpy.ast.Pass") i++;
                }
                else if (labelName.StartsWith("_call_", StringComparison.Ordinal))
                {
                    fromClause = labelName;
                    i++;
                    if (i + 1 < list.Count && list[i + 1] is ClassDict p2
                        && p2.ClassName == "renpy.ast.Pass") i++;
                }
            }

            EmitNode(sb, node, indent, fromClause);
            if (!IsUnsupported(node)) emitted++;
        }
        return emitted;
    }

    /// <summary>Emittiert einen Block und garantiert, dass er nicht leer ist —
    /// wenn keine echten Statements dabei sind (nur Kommentare oder gar nichts),
    /// wird ein <c>pass</c> ergänzt, damit Ren'Py den Block akzeptiert.
    /// Sonst würde der Ren'Py-Parser mit "init statement expects a non-empty
    /// block" abbrechen.</summary>
    private void EmitBlockNonEmpty(StringBuilder sb, IEnumerable statements, int indent)
    {
        int emitted = EmitBlock(sb, statements, indent);
        if (emitted == 0) AppendIndented(sb, indent, "pass");
    }

    private static readonly HashSet<string> KnownNodeClasses = new(StringComparer.Ordinal)
    {
        "renpy.ast.Label", "renpy.ast.Say", "renpy.ast.Menu", "renpy.ast.If",
        "renpy.ast.Jump", "renpy.ast.Call", "renpy.ast.Return", "renpy.ast.Show",
        "renpy.ast.Scene", "renpy.ast.Hide", "renpy.ast.With", "renpy.ast.Python",
        "renpy.ast.EarlyPython", "renpy.ast.Init", "renpy.ast.Pass",
        "renpy.ast.Define", "renpy.ast.Default", "renpy.ast.UserStatement",
        "renpy.ast.Image",
    };

    private static bool IsUnsupported(ClassDict node) => !KnownNodeClasses.Contains(node.ClassName);

    private static bool TryUnwrapSingletonInit(ClassDict init, out ClassDict child)
    {
        child = null!;
        int prio = init.GetValueOrDefault("priority") is int p ? p : 0;
        if (prio != 0) return false;
        if (init.GetValueOrDefault("block") is not IEnumerable block) return false;
        var kids = block.Cast<object?>().ToList();
        if (kids.Count != 1) return false;
        if (kids[0] is not ClassDict k) return false;
        if (k.ClassName is not ("renpy.ast.Define" or "renpy.ast.Default")) return false;
        child = k;
        return true;
    }

    private static bool IsCallSyncLabel(ClassDict node, string callTarget)
    {
        if (node.ClassName != "renpy.ast.Label") return false;
        string labelName = AsString(node.GetValueOrDefault("name") ?? node.GetValueOrDefault("_name"));
        return labelName == $"_call_{callTarget}";
    }

    private static string GetCallTarget(ClassDict callNode) =>
        AsString(callNode.GetValueOrDefault("label"));

    private void EmitNode(StringBuilder sb, ClassDict node, int indent, string? fromLabel = null)
    {
        switch (node.ClassName)
        {
            case "renpy.ast.Label": EmitLabel(sb, node, indent); break;
            case "renpy.ast.Say": EmitSay(sb, node, indent); break;
            case "renpy.ast.Menu": EmitMenu(sb, node, indent); break;
            case "renpy.ast.If": EmitIf(sb, node, indent); break;
            case "renpy.ast.Jump": EmitJump(sb, node, indent); break;
            case "renpy.ast.Call": EmitCall(sb, node, indent, fromLabel); break;
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
            case "renpy.ast.Image": EmitImage(sb, node, indent); break;
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
        EmitBlockNonEmpty(sb, node.GetValueOrDefault("block") as IEnumerable ?? Array.Empty<object>(), indent + 1);
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
            EmitBlockNonEmpty(sb, arr[2] as IEnumerable ?? Array.Empty<object>(), indent + 2);
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
            EmitBlockNonEmpty(sb, arr[1] as IEnumerable ?? Array.Empty<object>(), indent + 1);
            first = false;
        }
    }

    private static void EmitJump(StringBuilder sb, ClassDict node, int indent)
    {
        string target = AsString(node.GetValueOrDefault("target"));
        bool expr = node.GetValueOrDefault("expression") is bool b && b;
        AppendIndented(sb, indent, expr ? $"jump expression {target}" : $"jump {target}");
    }

    private static void EmitCall(StringBuilder sb, ClassDict node, int indent, string? fromLabel = null)
    {
        string target = AsString(node.GetValueOrDefault("label"));
        bool expr = node.GetValueOrDefault("expression") is bool b && b;
        string head = expr ? $"call expression {target}" : $"call {target}";
        if (!string.IsNullOrEmpty(fromLabel)) head += $" from {fromLabel}";
        AppendIndented(sb, indent, head);
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
        EmitBlockNonEmpty(sb, node.GetValueOrDefault("block") as IEnumerable ?? Array.Empty<object>(), indent + 1);
    }

    private static void EmitDefine(StringBuilder sb, ClassDict node, int indent, string keyword)
    {
        string name = AsString(node.GetValueOrDefault("varname") ?? node.GetValueOrDefault("name"));
        string code = AsString(node.GetValueOrDefault("code"));
        string op = AsString(node.GetValueOrDefault("operator")); // "=" usw.
        if (string.IsNullOrEmpty(op)) op = "=";
        AppendIndented(sb, indent, $"{keyword} {name} {op} {code}".TrimEnd());
    }

    private static void EmitImage(StringBuilder sb, ClassDict node, int indent)
    {
        // imgname ist ein Tupel wie ("day1_wakeup",) oder ("hero", "happy").
        var imgname = node.GetValueOrDefault("imgname");
        string name = imgname is IEnumerable en
            ? string.Join(" ", en.Cast<object?>().Select(AsString))
            : AsString(imgname);
        string code = AsString(node.GetValueOrDefault("code"));

        if (!string.IsNullOrEmpty(code))
        {
            AppendIndented(sb, indent, $"image {name} = {code}");
            return;
        }

        // Kein code → Ren'Py-ATL-Block-Syntax: `image name:` mit ATL-Statements
        // im Body. Volle ATL-Sprache (linear, ease, xpos, parallel, choice …)
        // ist Roadmap v0.5. Aktuell emittieren wir einen Platzhalter-Body mit
        // pass, damit Ren'Py wenigstens parsen kann — die Bewegungslogik geht
        // dabei verloren; für die brauchst du unrpyc.
        AppendIndented(sb, indent, $"image {name}:");
        var atl = node.GetValueOrDefault("atl");
        int atlCount = atl is ClassDict cd
            && cd.GetValueOrDefault("statements") is IEnumerable stmts
            ? stmts.Cast<object?>().Count()
            : 0;
        AppendIndented(sb, indent + 1,
            $"# <ATL-Block mit {atlCount} Statement(s) — für volle ATL-Ausgabe unrpyc verwenden>");
        AppendIndented(sb, indent + 1, "pass");
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
