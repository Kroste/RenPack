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
        // Immer LF, nie CRLF — Ren'Py-Quelldateien sind LF-normalisiert und
        // Tests vergleichen gegen "\n". sb.AppendLine() wuerde auf Windows
        // "\r\n" produzieren und den Windows-CI-Build brechen.
        sb.Append("# Decompiled by RenPack — Basic-Decompiler ohne vollständige Ren'Py-Sprachdeckung.\n");
        sb.Append("# Für Screens, ATL und komplexe Init-Blöcke bitte weiterhin unrpyc verwenden.\n");
        sb.Append('\n');

        // Vor dem Emit: Transform-Aufrufe mit Argumenten sammeln. Ren'Py
        // speichert die Transform-Parameter nicht im rpyc — wenn ein Screen
        // "at flying_transform(msg[…])" ruft und der Transform bei uns ohne
        // Parameter emittiert wird, interpretiert Ren'Py das Argument als
        // Child-Displayable → "Not a displayable: 0". Wir merken uns die
        // Namen inklusive Argument-Anzahl und ergänzen bei der Transform-
        // Deklaration passend viele Fallback-Parameter.
        _transformCallArgCount = CollectTransformCallArgCounts(statements);
        // Und die *echten* Parameternamen aus dem ATL-Body raten: jeder
        // Python-Identifier, der weder Keyword noch Ren'Py-Global ist, ist
        // mit hoher Wahrscheinlichkeit ein Parameter — sonst würde Ren'Py
        // ihn zur Laufzeit als NameError aufmachen.
        _transformParamNames = CollectTransformParamNames(statements);

        EmitBlock(sb, statements, indent: 0);
        return sb.ToString();
    }

    private Dictionary<string, int> _transformCallArgCount = new(StringComparer.Ordinal);
    private Dictionary<string, List<string>> _transformParamNames = new(StringComparer.Ordinal);

    private static Dictionary<string, int> CollectTransformCallArgCounts(IEnumerable statements)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        // Fängt `NAME(...)` — mit Klammern-Balancierung, damit
        // `flying_transform(msg["slot_index"])` als 1-arg zählt, nicht als
        // Text.
        var callPattern = new System.Text.RegularExpressions.Regex(
            @"([A-Za-z_][A-Za-z0-9_]*)\s*\(",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        void Record(string call)
        {
            foreach (System.Text.RegularExpressions.Match m in callPattern.Matches(call))
            {
                string name = m.Groups[1].Value;
                int openPos = m.Index + m.Length - 1; // Position der '('
                int argCount = CountArgs(call, openPos);
                if (!result.TryGetValue(name, out var prev) || prev < argCount)
                    result[name] = argCount;
            }
        }

        void Walk(object? o)
        {
            if (o is null || (o.GetType().IsClass && !seen.Add(o))) return;
            if (o is ClassDict cd)
            {
                if (cd.ClassName == "renpy.sl2.slast.SLDisplayable"
                    && cd.TryGetValue("keyword", out var kws) && kws is IEnumerable kwEnum)
                {
                    foreach (var kv in kwEnum)
                    {
                        if (kv is object[] arr && arr.Length >= 2
                            && AsString(arr[0]) == "at")
                        {
                            Record(ExtractPyExprString(arr[1]));
                        }
                    }
                }
                foreach (var v in cd.Values) Walk(v);
            }
            else if (o is IEnumerable en && o is not string)
                foreach (var v in en) Walk(v);
        }
        Walk(statements);
        return result;
    }

    /// <summary>Zählt die Top-Level-Komma-getrennten Argumente in einem
    /// Python-Call ab der Position der öffnenden Klammer. Respektiert
    /// verschachtelte Klammern und Strings, sodass
    /// <c>foo(a, [b, c], d)</c> als 3 Argumente gezählt wird, nicht 4.</summary>
    private static int CountArgs(string text, int openParenPos)
    {
        if (openParenPos >= text.Length || text[openParenPos] != '(') return 0;
        int i = openParenPos + 1;
        // Leere Klammern: ()
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i < text.Length && text[i] == ')') return 0;

        int depth = 0;      // Klammern-Tiefe (relative zu openParenPos)
        int args = 1;
        char strChar = '\0';
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (strChar != '\0')
            {
                if (c == '\\' && i + 1 < text.Length) { i++; continue; }
                if (c == strChar) strChar = '\0';
                continue;
            }
            if (c == '"' || c == '\'') { strChar = c; continue; }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0) return args;
                depth--;
            }
            else if (c == ',' && depth == 0) args++;
        }
        return args;
    }

    private static string ExtractPyExprString(object? v)
    {
        if (v is string s) return s;
        if (v is ClassDict cd)
        {
            if (cd.TryGetValue("__args__", out var av) && av is object[] { Length: > 0 } args
                && args[0] is string first) return first;
        }
        return v?.ToString() ?? "";
    }

    /// <summary>Walkt alle <c>renpy.ast.Transform</c>-Nodes und rät die
    /// Original-Parameternamen: jeder freie Python-Identifier im ATL-Body,
    /// der kein Python-Keyword, Ren'Py-Global oder ATL-Warper ist, dürfte
    /// ein Transform-Parameter sein — sonst würde er zur Laufzeit als
    /// <c>NameError</c> knallen. Reihenfolge = Reihenfolge des ersten
    /// Vorkommens im Body (grobe Näherung an die Original-Parameter-
    /// Signatur, exakt lässt es sich aus dem rpyc nicht rekonstruieren).</summary>
    private static Dictionary<string, List<string>> CollectTransformParamNames(IEnumerable statements)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(object? o)
        {
            if (o is null || (o.GetType().IsClass && !seen.Add(o))) return;
            if (o is ClassDict cd)
            {
                if (cd.ClassName == "renpy.ast.Transform")
                {
                    string name = AsString(cd.GetValueOrDefault("varname") ?? cd.GetValueOrDefault("name"));
                    var idents = new List<string>();
                    var uniq = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var expr in CollectPyExprStrings(cd.GetValueOrDefault("atl")))
                        foreach (var id in ExtractFreeIdentifiers(expr))
                            if (uniq.Add(id)) idents.Add(id);
                    if (idents.Count > 0) result[name] = idents;
                }
                foreach (var v in cd.Values) Walk(v);
            }
            else if (o is IEnumerable en && o is not string)
                foreach (var v in en) Walk(v);
        }
        Walk(statements);
        return result;
    }

    /// <summary>Sammelt alle in einem AST-Teilbaum enthaltenen PyExpr-
    /// Ausdrücke (Class-Namen enden mit <c>PyExpr</c> oder <c>PyCode</c>) —
    /// beim Unpickeln landet der Ausdruck-Text in <c>__args__[0]</c>.</summary>
    private static List<string> CollectPyExprStrings(object? root)
    {
        var results = new List<string>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(object? o)
        {
            if (o is null || (o.GetType().IsClass && !seen.Add(o))) return;
            if (o is ClassDict cd)
            {
                if ((cd.ClassName.EndsWith("PyExpr", StringComparison.Ordinal)
                     || cd.ClassName.EndsWith("PyCode", StringComparison.Ordinal))
                    && cd.TryGetValue("__args__", out var av)
                    && av is object[] { Length: >= 1 } args
                    && args[0] is string first
                    && !string.IsNullOrWhiteSpace(first))
                    results.Add(first);
                foreach (var v in cd.Values) Walk(v);
            }
            else if (o is IEnumerable en && o is not string)
                foreach (var v in en) Walk(v);
        }
        Walk(root);
        return results;
    }

    /// <summary>Python-Keywords, Builtins, Ren'Py-Namespaces und ATL-Warper —
    /// alles, was in einer PyExpr auftauchen kann, aber kein Parameter ist.</summary>
    private static readonly HashSet<string> NonParameterIdentifiers = new(StringComparer.Ordinal)
    {
        "True", "False", "None",
        "and", "or", "not", "if", "else", "for", "in", "is", "lambda",
        "int", "float", "str", "bool", "list", "dict", "tuple", "set",
        "range", "len", "min", "max", "abs", "round", "map", "filter", "sum",
        "any", "all", "sorted", "reversed", "enumerate", "zip",
        "renpy", "store", "persistent", "config", "gui", "preferences",
        "math", "random", "_", "_p",
        "linear", "ease", "easein", "easeout",
        "easein_quad", "easeout_quad", "easein_cubic", "easeout_cubic",
        "easein_quart", "easeout_quart", "easein_quint", "easeout_quint",
        "easein_expo", "easeout_expo", "easein_circ", "easeout_circ",
        "easein_back", "easeout_back", "easein_bounce", "easeout_bounce",
        "easein_elastic", "easeout_elastic",
        "pause", "time", "repeat", "parallel", "block", "choice", "on",
        "event", "function", "contains", "clockwise", "counterclockwise",
    };

    private static readonly System.Text.RegularExpressions.Regex FreeIdentifierRegex =
        new(@"(?<![\w.])([A-Za-z_][A-Za-z0-9_]*)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static IEnumerable<string> ExtractFreeIdentifiers(string expr)
    {
        // String-Literale ausblenden, damit "foo" darin nicht als Identifier zählt.
        string noStrings = System.Text.RegularExpressions.Regex.Replace(
            expr, @"'([^'\\]|\\.)*'|""([^""\\]|\\.)*""", "\"\"");
        foreach (System.Text.RegularExpressions.Match m in FreeIdentifierRegex.Matches(noStrings))
        {
            string id = m.Groups[1].Value;
            if (!NonParameterIdentifiers.Contains(id)) yield return id;
        }
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

            // Aufeinanderfolgende TranslateString-Nodes derselben Sprache in
            // einen gemeinsamen "translate LANG strings:"-Block zusammenfassen.
            // Ren'Py's Compiler wrappt jeden old/new-Paar in eine eigene Node,
            // aber die User-Syntax hat sie unter einem Block.
            if (node.ClassName == "renpy.ast.TranslateString")
            {
                string lang = AsString(node.GetValueOrDefault("language"));
                if (string.IsNullOrEmpty(lang)) lang = "None";
                AppendIndented(sb, indent, $"translate {lang} strings:");
                EmitTranslateStringEntry(sb, node, indent + 1);
                // Konsekutive TranslateStrings derselben Sprache mit-emittieren
                while (i + 1 < list.Count && list[i + 1] is ClassDict tsNext
                       && tsNext.ClassName == "renpy.ast.TranslateString"
                       && AsString(tsNext.GetValueOrDefault("language")) == lang)
                {
                    i++;
                    EmitTranslateStringEntry(sb, tsNext, indent + 1);
                }
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
        "renpy.ast.Image", "renpy.ast.Screen", "renpy.ast.Style",
        "renpy.ast.Transform",
        "renpy.ast.Translate", "renpy.ast.EndTranslate",
        "renpy.ast.TranslateString", "renpy.ast.TranslateBlock",
        "renpy.ast.TranslatePython", "renpy.ast.TranslateEarlyBlock",
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
            case "renpy.ast.Python": EmitPython(sb, node, indent, early: false); break;
            case "renpy.ast.EarlyPython": EmitPython(sb, node, indent, early: true); break;
            case "renpy.ast.Init": EmitInit(sb, node, indent); break;
            case "renpy.ast.Pass": AppendIndented(sb, indent, "pass"); break;
            case "renpy.ast.Define": EmitDefine(sb, node, indent, "define"); break;
            case "renpy.ast.Default": EmitDefine(sb, node, indent, "default"); break;
            case "renpy.ast.UserStatement": EmitUserStatement(sb, node, indent); break;
            case "renpy.ast.Image": EmitImage(sb, node, indent); break;
            case "renpy.ast.Screen": RenpySlWriter.EmitScreen(sb, node, indent); break;
            case "renpy.ast.Style": EmitStyle(sb, node, indent); break;
            case "renpy.ast.Transform": EmitTransform(sb, node, indent); break;
            case "renpy.ast.Translate": EmitTranslate(sb, node, indent); break;
            case "renpy.ast.EndTranslate": /* Marker, wird beim Emit übersprungen */ break;
            case "renpy.ast.TranslateString":
                // Wird niemals einzeln emittiert — der Block-Emit gruppiert
                // aufeinanderfolgende TranslateStrings in einen strings-Block.
                // Falls doch (fehlerhafter Input), als Kommentar durchlassen.
                AppendIndented(sb, indent, "# <renpy.ast.TranslateString ohne umschließenden strings-Block>");
                break;
            case "renpy.ast.TranslateBlock": EmitTranslateBlock(sb, node, indent); break;
            case "renpy.ast.TranslatePython": EmitTranslatePython(sb, node, indent); break;
            case "renpy.ast.TranslateEarlyBlock": EmitTranslateBlock(sb, node, indent, early: true); break;
            default:
                AppendIndented(sb, indent, $"# <unsupported: {node.ClassName}>");
                break;
        }
    }

    // ---- Node-spezifische Writer -------------------------------------------

    private void EmitLabel(StringBuilder sb, ClassDict node, int indent)
    {
        string name = AsString(node.GetValueOrDefault("name") ?? node.GetValueOrDefault("_name"));
        string parameters = FormatParameterInfo(node.GetValueOrDefault("parameters"));
        AppendIndented(sb, indent, $"label {name}{parameters}:");
        EmitBlockNonEmpty(sb, node.GetValueOrDefault("block") as IEnumerable ?? Array.Empty<object>(), indent + 1);
    }

    /// <summary>Formatiert ein <c>renpy.ast.ParameterInfo</c>-Node in seine
    /// <c>(name, name2=default, …)</c>-Deklarationsform fuer <c>label</c>-
    /// und <c>screen</c>-Definitionen. Struktur: <c>ParameterInfo.parameters
    /// = [(name, default?), …]</c>. Positional-Args haben <c>default = None</c>.</summary>
    private static string FormatParameterInfo(object? parameters)
    {
        if (parameters is not ClassDict cd) return "";
        if (cd.GetValueOrDefault("parameters") is not IEnumerable list) return "";
        var parts = new List<string>();
        foreach (var p in list)
        {
            if (p is not object[] arr || arr.Length < 1) continue;
            string pname = AsString(arr[0]);
            if (string.IsNullOrEmpty(pname)) continue;
            if (arr.Length >= 2 && arr[1] is not null)
                parts.Add($"{pname}={AsString(arr[1])}");
            else
                parts.Add(pname);
        }
        return parts.Count > 0 ? "(" + string.Join(", ", parts) + ")" : "";
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
        string args = FormatArgumentInfo(node.GetValueOrDefault("arguments"));
        string head = expr ? $"call expression {target}" : $"call {target}";
        if (!string.IsNullOrEmpty(args)) head += " " + args;
        if (!string.IsNullOrEmpty(fromLabel)) head += $" from {fromLabel}";
        AppendIndented(sb, indent, head);
    }

    /// <summary>Formatiert ein <c>renpy.ast.ArgumentInfo</c>-Node in seine
    /// <c>(arg1, kw=arg2, …)</c>-Textform. Wird von <see cref="EmitCall"/>
    /// gebraucht: ohne die Args wuerde <c>call unlock("x") from _foo</c>
    /// als <c>call unlock from _foo</c> emittiert, und der aufgerufene
    /// <c>label unlock(label_name)</c>-Parameter bekommt keinen Wert →
    /// <c>NameError: name 'label_name' is not defined</c> zur Laufzeit
    /// (verifiziert an Sophia Parker 0.230, v0.8.4-Bug).
    ///
    /// Struktur: <c>ArgumentInfo.arguments = [(name?, PyExpr), …]</c> —
    /// wenn <c>name</c> None ist, ist es positional; sonst Keyword-Arg.</summary>
    private static string FormatArgumentInfo(object? arguments)
    {
        if (arguments is not ClassDict cd) return "";
        if (cd.GetValueOrDefault("arguments") is not IEnumerable list) return "";
        var parts = new List<string>();
        foreach (var a in list)
        {
            if (a is not object[] arr || arr.Length < 2) continue;
            string aname = arr[0] is null ? "" : AsString(arr[0]);
            string aval = AsString(arr[1]);
            parts.Add(string.IsNullOrEmpty(aname) ? aval : $"{aname}={aval}");
        }
        return parts.Count > 0 ? "(" + string.Join(", ", parts) + ")" : "";
    }

    private static void EmitReturn(StringBuilder sb, ClassDict node, int indent)
    {
        var expr = node.GetValueOrDefault("expression");
        string suffix = expr is null ? "" : " " + AsString(expr);
        AppendIndented(sb, indent, $"return{suffix}");
    }

    /// <summary>Emittiert show/scene/hide-Statements. Die Imspec-Struktur ist
    /// ein Tupel mit variabler Länge:
    /// <c>(name-tuple, expression, tag, at_list, layer, zorder, behind)</c>.
    /// Neuere Ren'Py-Versionen können zusätzliche Felder haben — wir lesen nur
    /// die, die vorhanden sind.
    ///
    /// Wichtige Sonderfälle:
    /// <list type="bullet">
    ///   <item><c>expression</c> ist gesetzt → <c>scene expression &lt;expr&gt;</c>
    ///     (Python-Ausdruck statt festem Bild-Namen, z. B.
    ///     <c>renpy.random.choice([...])</c>). Sonst hätte Ren'Py mit
    ///     "end of line expected" abgebrochen.</item>
    ///   <item><c>tag</c> → <c>as TAG</c></item>
    ///   <item><c>at_list</c> → <c>at TRANSFORM[, …]</c></item>
    ///   <item><c>behind</c> → <c>behind TAG[, …]</c></item>
    ///   <item><c>layer</c> (non-Default "master") → <c>onlayer LAYER</c></item>
    ///   <item><c>zorder</c> → <c>zorder N</c></item>
    ///   <item><c>atl</c> auf dem Node → Body mit ATL-Block</item>
    /// </list></summary>
    private static void EmitShowHideScene(StringBuilder sb, ClassDict node, int indent, string keyword)
    {
        var imspec = node.GetValueOrDefault("imspec") as object[];
        var parts = new List<string> { keyword };

        object? name = null, expression = null, tag = null, atList = null,
            layer = null, zorder = null, behind = null;
        if (imspec is not null)
        {
            if (imspec.Length > 0) name = imspec[0];
            if (imspec.Length > 1) expression = imspec[1];
            if (imspec.Length > 2) tag = imspec[2];
            if (imspec.Length > 3) atList = imspec[3];
            if (imspec.Length > 4) layer = imspec[4];
            if (imspec.Length > 5) zorder = imspec[5];
            if (imspec.Length > 6) behind = imspec[6];
        }

        string exprText = expression is null ? "" : AsString(expression);
        if (!string.IsNullOrEmpty(exprText) && exprText != "None")
        {
            parts.Add("expression");
            parts.Add(exprText);
        }
        else if (name is IEnumerable nameParts && name is not string)
        {
            var tags = nameParts.Cast<object?>().Select(AsString)
                .Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (tags.Count > 0) parts.Add(string.Join(" ", tags));
        }
        else if (name is not null)
        {
            parts.Add(AsString(name));
        }

        if (tag is string tagStr && !string.IsNullOrEmpty(tagStr))
        {
            parts.Add("as");
            parts.Add(tagStr);
        }

        if (atList is IEnumerable atls)
        {
            var items = atls.Cast<object?>().Select(AsString)
                .Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (items.Count > 0)
            {
                parts.Add("at");
                parts.Add(string.Join(", ", items));
            }
        }

        if (behind is IEnumerable behinds)
        {
            var items = behinds.Cast<object?>().Select(AsString)
                .Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (items.Count > 0)
            {
                parts.Add("behind");
                parts.Add(string.Join(", ", items));
            }
        }

        if (layer is string layStr && !string.IsNullOrEmpty(layStr) && layStr != "master")
        {
            parts.Add("onlayer");
            parts.Add(layStr);
        }

        string zorderText = AsString(zorder);
        if (!string.IsNullOrEmpty(zorderText) && zorderText != "0" && zorderText != "None")
        {
            parts.Add("zorder");
            parts.Add(zorderText);
        }

        // ATL-Body (z. B. `show hero: linear 1.0 xpos 100`) — nur bei show/scene
        // relevant, hide hat nie einen atl-Block.
        var atl = node.GetValueOrDefault("atl");
        if (atl is ClassDict atlBlock && atlBlock.ClassName == "renpy.atl.RawBlock")
        {
            AppendIndented(sb, indent, string.Join(" ", parts) + ":");
            RenpyAtlWriter.EmitBlockBody(sb, atlBlock, indent + 1);
        }
        else
        {
            AppendIndented(sb, indent, string.Join(" ", parts));
        }
    }

    private static void EmitWith(StringBuilder sb, ClassDict node, int indent)
    {
        var expr = node.GetValueOrDefault("expr") ?? node.GetValueOrDefault("expression");
        AppendIndented(sb, indent, $"with {AsString(expr)}");
    }

    private static void EmitPython(StringBuilder sb, ClassDict node, int indent, bool early)
    {
        string code = AsString(node.GetValueOrDefault("code"));
        bool hide = node.GetValueOrDefault("hide") is bool h && h;
        bool store = node.GetValueOrDefault("store") is string s && s != "store";

        // Optionale hide/store-Modifier bauen die Header-Zeile mit — Reihenfolge:
        // `python [early] [hide] [in <store>]:`. Der $-Shortcut existiert NUR
        // für das einfache Python-Statement — nicht für early/hide/in.
        bool needsBlockForm = early || hide || store;

        if (!code.Contains('\n') && !needsBlockForm)
        {
            AppendIndented(sb, indent, $"$ {code}");
            return;
        }

        var header = new System.Text.StringBuilder("python");
        if (early) header.Append(" early");
        if (hide) header.Append(" hide");
        if (node.GetValueOrDefault("store") is string ns && !string.IsNullOrEmpty(ns) && ns != "store")
            header.Append(" in ").Append(ns);
        header.Append(':');
        AppendIndented(sb, indent, header.ToString());

        int lineCount = 0;
        foreach (var line in code.Split('\n'))
        {
            AppendIndented(sb, indent + 1, line);
            lineCount++;
        }
        // Ren'Py erwartet einen non-empty Block — leerer Python-Body kracht.
        if (lineCount == 0 || (lineCount == 1 && string.IsNullOrWhiteSpace(code)))
            AppendIndented(sb, indent + 1, "pass");
    }

    private void EmitInit(StringBuilder sb, ClassDict node, int indent)
    {
        int priority = node.GetValueOrDefault("priority") is int p ? p : 0;
        AppendIndented(sb, indent, $"init {priority}:");
        EmitBlockNonEmpty(sb, node.GetValueOrDefault("block") as IEnumerable ?? Array.Empty<object>(), indent + 1);
    }

    /// <summary>Emittiert <c>define</c>/<c>default</c>-Statements inklusive
    /// Store-Prefix. Wichtig: Der Ren'Py-Compiler speichert im <c>store</c>-Feld
    /// die volle Store-Bezeichnung — z. B. <c>"store"</c> (Default) oder
    /// <c>"store.gui"</c>. Im Original-.rpy schreibt der User <c>define gui.foo</c>,
    /// nicht <c>define foo</c>; wenn wir den Store-Prefix vergessen, landet die
    /// Variable im falschen Namespace und spätere <c>gui.foo</c>-Zugriffe werfen
    /// <c>AttributeError: 'StoreModule' object has no attribute 'foo'</c>.</summary>
    private static void EmitDefine(StringBuilder sb, ClassDict node, int indent, string keyword)
    {
        string varname = AsString(node.GetValueOrDefault("varname") ?? node.GetValueOrDefault("name"));
        string store = AsString(node.GetValueOrDefault("store"));
        string code = AsString(node.GetValueOrDefault("code"));
        string op = AsString(node.GetValueOrDefault("operator")); // "=", "+=", …
        if (string.IsNullOrEmpty(op)) op = "=";

        // "store"        → nur varname
        // "store.gui"    → gui.varname
        // "store.config" → config.varname
        string qualified = varname;
        if (!string.IsNullOrEmpty(store) && store != "store")
        {
            string prefix = store.StartsWith("store.", StringComparison.Ordinal)
                ? store[6..] : store;
            qualified = $"{prefix}.{varname}";
        }

        // Optional: Index-Zuweisung wie `define foo[0] = 42` — der Compiler
        // packt den Index-Ausdruck in ein "index"-Feld.
        var index = node.GetValueOrDefault("index");
        if (index is not null && AsString(index) is { Length: > 0 } idxStr && idxStr != "None")
            qualified += $"[{idxStr}]";

        AppendIndented(sb, indent, $"{keyword} {qualified} {op} {code}".TrimEnd());
    }

    private static void EmitTranslateStringEntry(StringBuilder sb, ClassDict node, int indent)
    {
        string oldStr = AsString(node.GetValueOrDefault("old"));
        string newStr = AsString(node.GetValueOrDefault("new"));
        AppendIndented(sb, indent, $"old \"{EscapeString(oldStr)}\"");
        AppendIndented(sb, indent, $"new \"{EscapeString(newStr)}\"");
    }

    /// <summary>Translate-Block: <c>translate LANG IDENTIFIER:</c> gefolgt von
    /// dem eigentlichen Body (meist ein einzelnes Say). Der Ren'Py-Compiler
    /// hängt danach automatisch einen <c>EndTranslate</c>-Marker an — den
    /// überspringen wir im Block-Walker.</summary>
    private void EmitTranslate(StringBuilder sb, ClassDict node, int indent)
    {
        string lang = AsString(node.GetValueOrDefault("language"));
        string identifier = AsString(node.GetValueOrDefault("identifier"));
        if (string.IsNullOrEmpty(lang)) lang = "None";
        AppendIndented(sb, indent, $"translate {lang} {identifier}:");
        EmitBlockNonEmpty(sb, node.GetValueOrDefault("block") as IEnumerable ?? Array.Empty<object>(), indent + 1);
    }

    /// <summary>TranslateBlock: <c>translate LANG python:</c> bzw.
    /// <c>translate LANG:</c> mit Init/Style/… im Body.</summary>
    private void EmitTranslateBlock(StringBuilder sb, ClassDict node, int indent, bool early = false)
    {
        string lang = AsString(node.GetValueOrDefault("language"));
        if (string.IsNullOrEmpty(lang)) lang = "None";
        string header = early ? $"translate {lang} early:" : $"translate {lang}:";
        AppendIndented(sb, indent, header);
        EmitBlockNonEmpty(sb, node.GetValueOrDefault("block") as IEnumerable ?? Array.Empty<object>(), indent + 1);
    }

    /// <summary>TranslatePython: <c>translate LANG python:</c> mit Python-Code.</summary>
    private static void EmitTranslatePython(StringBuilder sb, ClassDict node, int indent)
    {
        string lang = AsString(node.GetValueOrDefault("language"));
        if (string.IsNullOrEmpty(lang)) lang = "None";
        string code = AsString(node.GetValueOrDefault("code"));
        AppendIndented(sb, indent, $"translate {lang} python:");
        if (string.IsNullOrWhiteSpace(code))
            AppendIndented(sb, indent + 1, "pass");
        else
            foreach (var line in code.Split('\n'))
                AppendIndented(sb, indent + 1, line);
    }

    /// <summary>Transform-Deklaration auf Top-Level: <c>transform NAME:</c>
    /// gefolgt von einem ATL-Block. Wie ein Named-Image-mit-ATL, nur
    /// wiederverwendbar per <c>at NAME</c> auf beliebigen Displayables.
    ///
    /// Sonderfall: Ren'Py speichert die Transform-Parameter nicht in der
    /// .rpyc. Wir rekonstruieren aus den Aufrufern die Anzahl und aus dem
    /// ATL-Body die Namen. Aber Achtung: <b>keinen Parameter emittieren,
    /// wenn kein Aufrufer Argumente übergibt</b> — freie Identifier im Body
    /// (z. B. <c>linear scroll_duration yoffset -12000</c>) sind sonst
    /// Store-Variablen und würden durch einen fälschlich hinzugefügten
    /// Parameter geshadowed werden (Default <c>None</c> → TypeError beim
    /// Rendern). Nur wenn ein Aufruf tatsächlich Argumente übergibt (dann
    /// braucht Ren'Py eine Signatur, sonst Child-Displayable-Bug), rekon-
    /// struieren wir <c>callArgCount</c> Parameter — benannt mit den
    /// extrahierten Namen wo verfügbar, sonst <c>_argN</c> als Fallback.
    /// Ren'Py verbietet <c>*args</c>/<c>**kwargs</c> auf <c>transform</c>-
    /// Statements explizit, also nur benannte Parameter mit Default
    /// <c>None</c>.</summary>
    private void EmitTransform(StringBuilder sb, ClassDict node, int indent)
    {
        string name = AsString(node.GetValueOrDefault("varname") ?? node.GetValueOrDefault("name"));

        _transformCallArgCount.TryGetValue(name, out int callArgCount);
        _transformParamNames.TryGetValue(name, out var extractedNames);
        extractedNames ??= new List<string>();

        string paramSuffix = "";
        if (callArgCount > 0)
        {
            var parts = new List<string>(callArgCount);
            for (int i = 0; i < callArgCount; i++)
            {
                string paramName = i < extractedNames.Count ? extractedNames[i] : $"_arg{i}";
                parts.Add($"{paramName}=None");
            }
            paramSuffix = "(" + string.Join(", ", parts) + ")";
        }

        AppendIndented(sb, indent, $"transform {name}{paramSuffix}:");
        RenpyAtlWriter.EmitBlockBody(sb, node.GetValueOrDefault("atl"), indent + 1);
    }

    private static void EmitStyle(StringBuilder sb, ClassDict node, int indent)
    {
        string name = AsString(node.GetValueOrDefault("style_name") ?? node.GetValueOrDefault("name"));
        string parent = AsString(node.GetValueOrDefault("parent"));
        bool clear = node.GetValueOrDefault("clear") is bool c && c;
        var properties = node.GetValueOrDefault("properties") as IDictionary;
        var delattr = node.GetValueOrDefault("delattr") as IEnumerable;
        var take = node.GetValueOrDefault("take");
        var variant = AsString(node.GetValueOrDefault("variant"));

        string head = $"style {name}";
        if (!string.IsNullOrEmpty(parent) && parent != "None") head += $" is {parent}";

        // Erst den Body-Text sammeln, dann entscheiden ob wir überhaupt einen
        // Doppelpunkt setzen. Ren'Py erlaubt "style X" ohne Body — dort kein
        // "pass" einfügen, weil Ren'Py "pass" nicht als Style-Property kennt
        // ("style property pass is not known").
        var bodyLines = new List<string>();
        if (clear) bodyLines.Add("clear");
        if (take is not null && AsString(take) is { Length: > 0 } takeStr && takeStr != "None")
            bodyLines.Add($"take {takeStr}");
        if (!string.IsNullOrEmpty(variant) && variant != "None")
            bodyLines.Add($"variant {variant}");
        if (delattr is not null)
            foreach (var d in delattr) bodyLines.Add($"del {AsString(d)}");
        if (properties is not null)
            foreach (DictionaryEntry de in properties)
                bodyLines.Add($"{AsString(de.Key)} {AsString(de.Value)}");

        if (bodyLines.Count == 0)
        {
            AppendIndented(sb, indent, head);
            return;
        }

        AppendIndented(sb, indent, head + ":");
        foreach (var line in bodyLines) AppendIndented(sb, indent + 1, line);
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
        // im Body. Der ATL-Writer deckt die häufigen Raw*-Typen ab
        // (Multipurpose, Time, Parallel, Choice, Block, Repeat, On, Function,
        // Event, ContainsExpr); unbekannte Raw-Klassen werden als Kommentar
        // mit pass emittiert.
        AppendIndented(sb, indent, $"image {name}:");
        RenpyAtlWriter.EmitBlockBody(sb, node.GetValueOrDefault("atl"), indent + 1);
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
        sb.Append(content).Append('\n');
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
