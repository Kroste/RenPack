using System.Text.RegularExpressions;
using NLog;

namespace RenPack.Services.Modding;

/// <summary>
/// Parst dekompilierte Ren'Py-Skripte (.rpy) und extrahiert die Muster,
/// die fuer den Kroste-Mod-Generator gebraucht werden: Choices in
/// <c>menu:</c>-Bloecken samt Wirkung (welche Store-Variablen werden wie
/// veraendert), Store-Variablen aus <c>default</c>-Statements,
/// Character-Definitionen.
///
/// **Warum ein eigener .rpy-Parser statt AST vom Decompiler?** Der
/// Decompiler-Weg .rpyc→ClassDict→.rpy funktioniert gut fuer Story-
/// Skripte, aber der Modder-Workflow ist typischerweise: erst
/// dekompilieren (unser <see cref="RpycBatchService"/>), dann analysieren
/// und modden. Zu diesem Zeitpunkt liegen die .rpy-Dateien schon flach da,
/// und ein direkter Line-Parser ist deutlich weniger Aufwand als
/// nochmal durch die ClassDict-Ebene zu gehen.
///
/// Der Parser respektiert Ren'Py-typische Python-Indentation und deckt
/// die haeufigen Muster ab (menu, choice, $-Statements, if-Blocks). Bei
/// exotischen Syntax-Kombinationen liefert er einfach das was er
/// erkennt — ohne zu crashen.
/// </summary>
public sealed class RenpyModAnalyzer
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // ---- Regex-Muster ------------------------------------------------------

    /// <summary><c>label name:</c> oder <c>label name(args):</c>.</summary>
    private static readonly Regex LabelPattern = new(
        @"^\s*label\s+([A-Za-z_][A-Za-z0-9_]*)(?:\([^)]*\))?\s*:",
        RegexOptions.Compiled);

    /// <summary><c>menu:</c> oder <c>menu name:</c>.</summary>
    private static readonly Regex MenuPattern = new(
        @"^(\s*)menu\s*(?:[A-Za-z_][A-Za-z0-9_]*)?\s*:",
        RegexOptions.Compiled);

    /// <summary>Choice-Header: <c>"text":</c> oder <c>"text" (if cond):</c>.
    /// Der Text darf Escape-Sequenzen enthalten. Wir extrahieren den Rohtext
    /// zwischen den Anfuehrungszeichen und optional die Condition dahinter.</summary>
    private static readonly Regex ChoicePattern = new(
        @"^(\s*)""((?:[^""\\]|\\.)*)""\s*(?:\(([^)]+)\))?\s*(?:if\s+(.+?))?\s*:",
        RegexOptions.Compiled);

    /// <summary><c>$ var op value</c> — Python-Statement mit Zuweisung.
    /// Ops: <c>=  +=  -=  *=  /=</c>. Wir speichern das RHS als String —
    /// die Bewertung passiert spaeter im Generator (der aus <c>+= 3</c>
    /// ein Tag "+3" macht).</summary>
    private static readonly Regex DollarAssignPattern = new(
        @"^\s*\$\s*([A-Za-z_][A-Za-z0-9_.]*)\s*(=|\+=|-=|\*=|/=)\s*(.+?)\s*$",
        RegexOptions.Compiled);

    /// <summary><c>default varname = value</c>.</summary>
    private static readonly Regex DefaultPattern = new(
        @"^\s*default\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.+?)\s*$",
        RegexOptions.Compiled);

    /// <summary><c>define X = Character("Name"[, color="#ff0000"[, …]])</c>.
    /// Wir extrahieren nur Name und Color — den Rest liest der Rename-Patcher
    /// spaeter direkt aus der Original-Zeile.</summary>
    private static readonly Regex CharacterPattern = new(
        @"^\s*define\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*Character\s*\(\s*""((?:[^""\\]|\\.)*)""(?:\s*,\s*color\s*=\s*""(#?[0-9A-Fa-f]{3,8})"")?",
        RegexOptions.Compiled);

    /// <summary>Analysiert alle <c>.rpy</c>-Dateien unter <paramref name="rootDir"/>
    /// rekursiv. Auto-Sync- und Compiler-generierte Files (Endung <c>.rpymc</c>,
    /// Startpraefix <c>_</c>, oder Verzeichnisse <c>tl/</c> für Translations)
    /// werden ausgelassen.</summary>
    public ModAnalysis Analyze(string rootDir)
    {
        if (!Directory.Exists(rootDir))
            throw new DirectoryNotFoundException($"Analyse-Root nicht gefunden: {rootDir}");

        var choices = new List<RpyChoice>();
        var vars = new List<RpyStoreVariable>();
        var chars = new List<RpyCharacter>();
        var files = new List<string>();

        var root = Path.GetFullPath(rootDir);
        foreach (var file in Directory.EnumerateFiles(root, "*.rpy", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            // tl/ = Übersetzungen (haben eigene menu-Kopien, wuerden Duplikate
            // erzeugen). Kann auf jeder Tiefe liegen (game/tl/, tl/, …).
            if (rel.Split('/').Any(s => s.Equals("tl", StringComparison.OrdinalIgnoreCase)))
                continue;
            files.Add(rel);
            AnalyzeFile(file, rel, choices, vars, chars);
        }

        Log.Info("Mod-Analyse: {files} .rpy-Dateien, {choices} Choices, "
            + "{vars} Store-Variablen, {chars} Characters",
            files.Count, choices.Count, vars.Count, chars.Count);
        return new ModAnalysis(choices, vars, chars, files);
    }

    private static void AnalyzeFile(string absPath, string relPath,
        List<RpyChoice> choices, List<RpyStoreVariable> vars, List<RpyCharacter> chars)
    {
        var lines = File.ReadAllLines(absPath);
        string currentLabel = "";
        int menuIndexInLabel = -1;

        // Stack der aktiven menu-Bloecke: bei mehreren geschachtelten Menus
        // (menu → choice → nested menu innerhalb des Choice-Bodys) muessen
        // wir wissen zu welchem menu die aktuelle Choice-Zeile gehoert.
        // Element: (indent-des-menu, indent-fuer-choices, choice-index-counter)
        var menuStack = new Stack<MenuContext>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;

            int indent = line.Length - trimmed.Length;

            // Pop menus, deren Scope wir verlassen haben (aktuelle Zeile
            // ist gleich oder weniger eingerueckt als der menu-header).
            while (menuStack.Count > 0 && indent <= menuStack.Peek().MenuIndent)
                menuStack.Pop();

            // Label-Header?
            var mLabel = LabelPattern.Match(line);
            if (mLabel.Success)
            {
                currentLabel = mLabel.Groups[1].Value;
                menuIndexInLabel = -1;
                menuStack.Clear();
                continue;
            }

            // menu:-Header?
            var mMenu = MenuPattern.Match(line);
            if (mMenu.Success)
            {
                menuIndexInLabel++;
                menuStack.Push(new MenuContext(
                    MenuIndent: indent,
                    ChoiceIndent: -1, // wird beim ersten Choice gesetzt
                    ChoiceCount: 0,
                    LabelAtOpen: currentLabel,
                    MenuIndex: menuIndexInLabel));
                continue;
            }

            // Choice-Zeile? Nur wenn wir in einem menu-Scope sind.
            if (menuStack.Count > 0)
            {
                var mChoice = ChoicePattern.Match(line);
                if (mChoice.Success)
                {
                    var ctx = menuStack.Peek();
                    if (ctx.ChoiceIndent < 0)
                        ctx = ctx with { ChoiceIndent = indent };
                    // Nur Choices auf der erwarteten Indent-Ebene zaehlen —
                    // sonst wuerden Say-Statements in Choice-Bodies als Choice
                    // fehlinterpretiert.
                    if (indent == ctx.ChoiceIndent)
                    {
                        string text = UnescapeRenpyString(mChoice.Groups[2].Value);
                        string? condition = mChoice.Groups[4].Success
                            ? mChoice.Groups[4].Value.Trim() : null;

                        var deltas = CollectChoiceBodyDeltas(lines, i + 1, indent);
                        choices.Add(new RpyChoice(
                            SourceFile: relPath,
                            SourceLine: i + 1,
                            Label: ctx.LabelAtOpen,
                            MenuIndex: ctx.MenuIndex,
                            ChoiceIndex: ctx.ChoiceCount,
                            Text: text,
                            Condition: condition,
                            Deltas: deltas));

                        // menuStack.Peek() ist ein Struct-Copy — wir ersetzen den
                        // Kopf explizit mit dem aktualisierten Zaehler.
                        menuStack.Pop();
                        menuStack.Push(ctx with { ChoiceCount = ctx.ChoiceCount + 1 });
                    }
                }
            }

            // default varname = ...
            var mDefault = DefaultPattern.Match(line);
            if (mDefault.Success)
            {
                var val = StripLineComment(mDefault.Groups[2].Value).Trim();
                vars.Add(new RpyStoreVariable(
                    Name: mDefault.Groups[1].Value,
                    DefaultValue: val,
                    TypeInferred: InferType(val)));
            }

            // define X = Character(...)
            var mChar = CharacterPattern.Match(line);
            if (mChar.Success)
            {
                chars.Add(new RpyCharacter(
                    VarName: mChar.Groups[1].Value,
                    DisplayName: UnescapeRenpyString(mChar.Groups[2].Value),
                    Color: mChar.Groups[3].Success ? mChar.Groups[3].Value : null));
            }
        }
    }

    /// <summary>Sammelt <c>$ var op value</c>-Statements im Body eines
    /// Choices — aber nur die DIREKTEN Deltas, nicht die aus
    /// verschachtelten <c>menu:</c>-Bloecken. Sobald ein inneres
    /// <c>menu:</c> aufgemacht wird, ueberspringen wir alle Zeilen bis
    /// wir wieder auf oder unter dessen Indent-Level zurueck sind.</summary>
    private static IReadOnlyList<VarDelta> CollectChoiceBodyDeltas(
        string[] lines, int startIndex, int choiceIndent)
    {
        var deltas = new List<VarDelta>();
        int? skipUntilIndent = null; // wenn gesetzt: alles ignorieren
                                     // bis Indent <= skipUntilIndent

        for (int i = startIndex; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) continue;
            int indent = line.Length - trimmed.Length;
            if (indent <= choiceIndent) break;
            if (trimmed[0] == '#') continue;

            // Skip-Modus: warten bis wir aus dem verschachtelten Block raus sind
            if (skipUntilIndent is int skip)
            {
                if (indent <= skip) skipUntilIndent = null;
                else continue;
            }

            // Verschachteltes menu: entdeckt → alle deeper-indented Zeilen
            // ueberspringen (ihre Deltas gehoeren zu ihren eigenen Choices)
            var mMenu = MenuPattern.Match(line);
            if (mMenu.Success)
            {
                skipUntilIndent = indent;
                continue;
            }

            var m = DollarAssignPattern.Match(line);
            if (m.Success)
            {
                deltas.Add(new VarDelta(
                    Variable: m.Groups[1].Value,
                    Op: m.Groups[2].Value,
                    Value: StripLineComment(m.Groups[3].Value).Trim()));
            }
        }
        return deltas;
    }

    /// <summary>Entfernt einen trailing <c>#</c>-Kommentar aus einer
    /// Python-Value-Zeile. Beachtet String-Literale — <c>"hallo # welt"</c>
    /// bleibt intakt.</summary>
    private static string StripLineComment(string value)
    {
        char inString = '\0';
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (inString != '\0')
            {
                if (c == '\\' && i + 1 < value.Length) { i++; continue; }
                if (c == inString) inString = '\0';
            }
            else if (c is '"' or '\'') inString = c;
            else if (c == '#') return value[..i].TrimEnd();
        }
        return value;
    }

    /// <summary>Ren'Py-Escape-Sequenzen im Choice-Text zurueckwandeln
    /// (nur die haeufigen: <c>\n \" \\</c>).</summary>
    private static string UnescapeRenpyString(string raw) => raw
        .Replace("\\\"", "\"")
        .Replace("\\n", "\n")
        .Replace("\\\\", "\\");

    private static string InferType(string value)
    {
        if (value is "True" or "False") return "bool";
        if (value == "None") return "None";
        if (value.StartsWith('"') || value.StartsWith('\'')) return "str";
        if (long.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out _)) return "int";
        if (double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _)) return "float";
        if (value.StartsWith('[')) return "list";
        if (value.StartsWith('{')) return "dict";
        return "expr";
    }

    private sealed record MenuContext(
        int MenuIndent, int ChoiceIndent, int ChoiceCount,
        string LabelAtOpen, int MenuIndex);
}
