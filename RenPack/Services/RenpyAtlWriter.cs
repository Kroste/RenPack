using System.Collections;
using System.Globalization;
using System.Text;
using Razorvine.Pickle.Objects;

namespace RenPack.Services;

/// <summary>
/// Schreibt Ren'Py-ATL (Animation and Transformation Language) aus dem
/// dekompilierten AST zurück. ATL ist eine eigene DSL innerhalb von Ren'Py:
/// <code>
/// image flash:
///     alpha 1.0
///     pause 0.05
///     ease 0.3 alpha 0.0
///     linear 0.5 xoffset 20 yoffset 30
/// </code>
/// Jedes Statement ist ein <c>renpy.atl.Raw*</c>-Node — dieser Writer deckt
/// die häufigsten ab (Multipurpose, Time, Parallel, Choice, Block, Repeat,
/// On, Function, Event, ContainsExpr). Unbekannte Raw-Klassen werden als
/// <c># &lt;unsupported ATL: mod.Class&gt;</c>-Kommentar mit anschließendem
/// <c>pass</c> emittiert, damit Ren'Py den Block trotzdem parst.
/// </summary>
internal static class RenpyAtlWriter
{
    /// <summary>Emittiert den Body eines <c>renpy.atl.RawBlock</c> (aus dem
    /// <c>statements</c>-Feld) mit gegebener Indentation. Callee garantiert:
    /// gibt mindestens ein Statement aus (Fallback <c>pass</c>).</summary>
    public static void EmitBlockBody(StringBuilder sb, object? rawBlock, int indent)
    {
        int emitted = 0;
        if (rawBlock is ClassDict block && block.ClassName == "renpy.atl.RawBlock"
            && block.GetValueOrDefault("statements") is IEnumerable stmts)
        {
            foreach (var s in stmts)
            {
                if (EmitStatement(sb, s, indent)) emitted++;
            }
        }
        if (emitted == 0)
        {
            AppendIndented(sb, indent, "pass");
        }
    }

    private static bool EmitStatement(StringBuilder sb, object? stmt, int indent)
    {
        if (stmt is not ClassDict cd)
        {
            if (stmt is not null) AppendIndented(sb, indent, $"# <ATL-nicht-ClassDict: {stmt.GetType().Name}>");
            return false;
        }
        switch (cd.ClassName)
        {
            case "renpy.atl.RawMultipurpose":
                AppendIndented(sb, indent, EmitMultipurpose(cd));
                return true;
            case "renpy.atl.RawTime":
                AppendIndented(sb, indent, $"time {AsAtl(cd.GetValueOrDefault("time"))}");
                return true;
            case "renpy.atl.RawParallel":
                AppendIndented(sb, indent, "parallel:");
                EmitBlocksList(sb, cd, "blocks", indent + 1);
                return true;
            case "renpy.atl.RawChoice":
                EmitChoice(sb, cd, indent);
                return true;
            case "renpy.atl.RawBlock":
                AppendIndented(sb, indent, "block:");
                EmitBlockBody(sb, cd, indent + 1);
                return true;
            case "renpy.atl.RawRepeat":
                var count = AsAtl(cd.GetValueOrDefault("repeats"));
                AppendIndented(sb, indent, string.IsNullOrEmpty(count) ? "repeat" : $"repeat {count}");
                return true;
            case "renpy.atl.RawOn":
                EmitOn(sb, cd, indent);
                return true;
            case "renpy.atl.RawFunction":
                AppendIndented(sb, indent, $"function {AsAtl(cd.GetValueOrDefault("expr"))}");
                return true;
            case "renpy.atl.RawEvent":
                AppendIndented(sb, indent, $"event {AsAtl(cd.GetValueOrDefault("name"))}");
                return true;
            case "renpy.atl.RawContainsExpr":
                AppendIndented(sb, indent, $"contains {AsAtl(cd.GetValueOrDefault("expression"))}");
                return true;
            default:
                AppendIndented(sb, indent, $"# <unsupported ATL: {cd.ClassName}>");
                AppendIndented(sb, indent, "pass"); // damit übergeordneter Block valide bleibt
                return true;
        }
    }

    /// <summary><c>RawMultipurpose</c> ist der Kern-Node: eine einzelne
    /// Interpolations- oder Zuweisungs-Zeile. Format:
    /// <c>[warper] [duration] [property value]* [revolution] [circles N]</c>.
    /// Sonderfall <c>warper == "pause"</c>: nur <c>pause &lt;duration&gt;</c>.</summary>
    private static string EmitMultipurpose(ClassDict cd)
    {
        var warper = AsString(cd.GetValueOrDefault("warper"));
        var duration = AsAtl(cd.GetValueOrDefault("duration"));

        if (warper == "pause") return string.IsNullOrEmpty(duration) ? "pause" : $"pause {duration}";

        var parts = new List<string>();

        // Bild-Referenzen (contains-like): `expressions` = [(pyexpr, with_expr?), …]
        if (cd.GetValueOrDefault("expressions") is IEnumerable exprs)
        {
            foreach (var e in exprs)
            {
                if (e is object[] arr && arr.Length >= 1) parts.Add(AsAtl(arr[0]));
                else parts.Add(AsAtl(e));
            }
        }

        if (!string.IsNullOrEmpty(warper)) parts.Add(warper);
        if (!string.IsNullOrEmpty(duration) && duration != "0") parts.Add(duration);

        // properties = [(name, value), …]
        if (cd.GetValueOrDefault("properties") is IEnumerable props)
        {
            foreach (var p in props)
            {
                if (p is object[] arr && arr.Length >= 2)
                {
                    parts.Add(AsString(arr[0]));
                    parts.Add(AsAtl(arr[1]));
                }
            }
        }

        // revolution: "clockwise" oder "counterclockwise"
        var revolution = cd.GetValueOrDefault("revolution");
        if (revolution is string rev && !string.IsNullOrEmpty(rev)) parts.Add(rev);

        // circles: N Umdrehungen
        var circles = AsAtl(cd.GetValueOrDefault("circles"));
        if (!string.IsNullOrEmpty(circles) && circles != "0") parts.Add($"circles {circles}");

        // splines = [(name, [(px, py, cp1x, cp1y, cp2x, cp2y), …])]
        if (cd.GetValueOrDefault("splines") is IEnumerable splines)
        {
            foreach (var sp in splines)
            {
                if (sp is object[] arr && arr.Length >= 2)
                {
                    parts.Add(AsString(arr[0]));
                    parts.Add("knot");
                    parts.Add(AsAtl(arr[1]));
                }
            }
        }

        return parts.Count == 0 ? "pass" : string.Join(" ", parts);
    }

    private static void EmitChoice(StringBuilder sb, ClassDict cd, int indent)
    {
        // choice = [(chance, RawBlock), …]
        if (cd.GetValueOrDefault("choices") is not IEnumerable choices)
        {
            AppendIndented(sb, indent, "choice:");
            AppendIndented(sb, indent + 1, "pass");
            return;
        }
        foreach (var c in choices)
        {
            if (c is not object[] arr || arr.Length < 2) continue;
            string chance = AsAtl(arr[0]);
            string head = string.IsNullOrEmpty(chance) || chance == "1.0" ? "choice:" : $"choice {chance}:";
            AppendIndented(sb, indent, head);
            EmitBlockBody(sb, arr[1], indent + 1);
        }
    }

    private static void EmitOn(StringBuilder sb, ClassDict cd, int indent)
    {
        // handlers = { event_name : RawBlock, … }
        if (cd.GetValueOrDefault("handlers") is not IDictionary handlers)
        {
            AppendIndented(sb, indent, "on unknown:");
            AppendIndented(sb, indent + 1, "pass");
            return;
        }
        foreach (DictionaryEntry de in handlers)
        {
            AppendIndented(sb, indent, $"on {AsString(de.Key)}:");
            EmitBlockBody(sb, de.Value, indent + 1);
        }
    }

    private static void EmitBlocksList(StringBuilder sb, ClassDict cd, string field, int indent)
    {
        if (cd.GetValueOrDefault(field) is not IEnumerable blocks)
        {
            AppendIndented(sb, indent, "pass");
            return;
        }
        int total = 0;
        foreach (var b in blocks)
        {
            AppendIndented(sb, indent, "block:");
            EmitBlockBody(sb, b, indent + 1);
            total++;
        }
        if (total == 0) AppendIndented(sb, indent, "pass");
    }

    // ---- Value-Formatierung ------------------------------------------------

    /// <summary>Wandelt einen ATL-Wert in seine Textrepräsentation. PyExpr wird
    /// zum String entpackt, Zahlen/Strings direkt.</summary>
    private static string AsAtl(object? v) => v switch
    {
        null => "",
        string s => s,
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        ClassDict cd => ExtractPyExpr(cd),
        object[] arr => "(" + string.Join(", ", arr.Select(AsAtl)) + ")",
        IEnumerable en => "[" + string.Join(", ", en.Cast<object?>().Select(AsAtl)) + "]",
        _ => v.ToString() ?? "",
    };

    private static string AsString(object? v) => v?.ToString() ?? "";

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
        for (int i = 0; i < indent; i++) sb.Append("    ");
        // Immer LF, nie CRLF — Konsistenz mit RenpyRpycDecompiler.
        sb.Append(content).Append('\n');
    }
}
