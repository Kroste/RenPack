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

    /// <summary><c>$ obj.update("attr", value)</c> — Character-Method-Pattern
    /// haeufig in Ren'Py-Spielen wo Stats ueber Character/Container-Klassen
    /// gehalten werden statt als direkte Store-Vars (z.B.
    /// <c>$ fcs.update('morality', 1)</c> in Boundaries of Morality).
    /// Wir behandeln das als additive Delta wenn der Value numeric ist,
    /// sonst als Assign.</summary>
    private static readonly Regex DollarUpdateCallPattern = new(
        @"^\s*\$\s*([A-Za-z_][A-Za-z0-9_.]*)\s*\.\s*update\s*\(\s*['""]([A-Za-z_][A-Za-z0-9_]*)['""]\s*,\s*(.+?)\s*\)\s*$",
        RegexOptions.Compiled);

    /// <summary><c>default varname = value</c>.</summary>
    private static readonly Regex DefaultPattern = new(
        @"^\s*default\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.+?)\s*$",
        RegexOptions.Compiled);

    /// <summary><c>$ varname = value</c> — reine Zuweisung (nicht <c>+=</c>).
    /// Wird als implizite Store-Variable erfasst, damit Cheat-Menue-Kandidaten
    /// wie Interview-Desires' <c>$ keys = 0</c> nicht durchrutschen.</summary>
    private static readonly Regex DollarInitPattern = new(
        @"^\s*\$\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.+?)\s*$",
        RegexOptions.Compiled);

    /// <summary><c>define X = Character("Name"[, color="#ff0000"[, …]])</c>.
    /// Wir extrahieren nur Name und Color — den Rest liest der Rename-Patcher
    /// spaeter direkt aus der Original-Zeile.</summary>
    private static readonly Regex CharacterPattern = new(
        @"^\s*define\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*Character\s*\(\s*""((?:[^""\\]|\\.)*)""(?:\s*,\s*color\s*=\s*""(#?[0-9A-Fa-f]{3,8})"")?",
        RegexOptions.Compiled);

    /// <summary>Condition-Header: <c>if X:</c>, <c>elif Y:</c>, <c>while Z:</c>.
    /// Wir extrahieren nur den Ausdruck, den Rest interpretiert der Konsument.</summary>
    private static readonly Regex ConditionPattern = new(
        @"^\s*(?:if|elif|while)\s+(.+?)\s*:\s*$",
        RegexOptions.Compiled);

    /// <summary>Python-Identifier — fuer die Extraktion von Variablennamen aus
    /// Condition-Ausdruecken. Ren'Py-Store-Vars sind i.d.R. snake_case.</summary>
    private static readonly Regex IdentifierPattern = new(
        @"\b([a-zA-Z_][a-zA-Z_0-9]*)\b",
        RegexOptions.Compiled);

    /// <summary><c>jump X</c> oder <c>call X</c> — Kontrollfluss-Sprung.
    /// Wir folgen in <see cref="CollectChoiceBodyDeltas"/>, damit Choices
    /// deren Body nur aus einem Jump besteht (typisch bei
    /// „Weiche in ein anderes Label"-Menus) trotzdem ihren Impact melden.</summary>
    private static readonly Regex JumpPattern = new(
        @"^\s*(?:jump|call)\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    /// <summary>Say-Statement: <c>character "text"</c> oder nur <c>"text"</c>
    /// (Narrator). Der Text kann Escape-Sequenzen enthalten. Wir extrahieren
    /// den optionalen Character-Identifier und den Raw-Text (mit Escapes).
    /// Modifiers wie <c>nointeract</c> oder <c>(what_prefix="X")</c> ignorieren
    /// wir fuer den E4b-Body-Rewrite (KI kriegt nur den reinen Text).</summary>
    private static readonly Regex SayPattern = new(
        @"^\s*(?:([A-Za-z_][A-Za-z0-9_]*)\s+)?""((?:[^""\\]|\\.)*)""\s*(?:\s+\w+)?\s*$",
        RegexOptions.Compiled);

    /// <summary>Python-Keywords + haeufige Builtins/Ren'Py-Funcs, die wir aus
    /// Consumer-Kandidaten ausfiltern (kein Store-Var, sondern Sprach-Element).</summary>
    private static readonly HashSet<string> NonVariableTokens = new(StringComparer.Ordinal)
    {
        "and", "or", "not", "in", "is", "if", "else", "elif", "True", "False", "None",
        "len", "int", "str", "float", "bool", "list", "dict", "set", "tuple",
        "range", "print", "type", "isinstance", "abs", "min", "max", "sum",
        "renpy", "config", "persistent", "store",
    };

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
        var varNamesSeen = new HashSet<string>(StringComparer.Ordinal);
        var chars = new List<RpyCharacter>();
        var files = new List<string>();
        var consumers = new Dictionary<string, List<VarConsumer>>(StringComparer.Ordinal);
        var menuLocations = new List<(string file, int line)>();
        var says = new List<RpySayStatement>();
        var globalDeltas = new List<VarDelta>();

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
            AnalyzeFile(file, rel, choices, vars, varNamesSeen, chars,
                consumers, menuLocations, says, globalDeltas);
        }

        // Nachtraeglich: Choice-Conditions als MenuChoiceGate-Consumer erfassen.
        // Machen wir hier statt in AnalyzeFile, weil wir dort die volle
        // Choice-Liste erst nach dem File-Lauf haben.
        foreach (var ch in choices)
        {
            if (string.IsNullOrWhiteSpace(ch.Condition)) continue;
            foreach (var v in ExtractIdentifiers(ch.Condition!))
                AddConsumer(consumers, v, new VarConsumer(
                    ch.SourceFile, ch.SourceLine, ch.Label,
                    VarConsumerKind.MenuChoiceGate, ch.Condition!));
        }

        // Frozen dictionary: pro Variable Consumers sortiert nach Datei/Zeile.
        var frozen = consumers.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<VarConsumer>)kv.Value
                .OrderBy(c => c.SourceFile, StringComparer.Ordinal)
                .ThenBy(c => c.SourceLine)
                .ToList());

        // Menu-Locations: pro (file, menuHeaderLine) alle Variables die
        // von den Choices dieses Menus veraendert werden. Wir matchen Choices
        // per (file, headerLine) → nehmen alle deren Deltas.
        var menuList = new List<RpyMenuLocation>(menuLocations.Count);
        foreach (var (mFile, mLine) in menuLocations)
        {
            var affectedVars = choices
                .Where(c => c.SourceFile == mFile && c.MenuHeaderLine == mLine)
                .SelectMany(c => c.Deltas)
                .Select(d => d.Variable)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToList();
            if (affectedVars.Count > 0)
                menuList.Add(new RpyMenuLocation(mFile, mLine, affectedVars));
        }

        Log.Info("Mod-Analyse: {files} .rpy-Dateien, {choices} Choices, "
            + "{vars} Store-Variablen, {chars} Characters, {consumers} Variables mit Consumers, "
            + "{menus} Menu-Locations mit Impact, {says} Say-Statements, {global} Global-Deltas",
            files.Count, choices.Count, vars.Count, chars.Count, frozen.Count, menuList.Count, says.Count, globalDeltas.Count);
        return new ModAnalysis(choices, vars, chars, files, frozen, menuList, says, globalDeltas);
    }

    private static void AddConsumer(Dictionary<string, List<VarConsumer>> dict,
        string varName, VarConsumer c)
    {
        if (!dict.TryGetValue(varName, out var list))
            dict[varName] = list = new List<VarConsumer>();
        list.Add(c);
    }

    /// <summary>Extrahiert Python-Identifier aus einem Ausdruck. Filtert
    /// Keywords/Builtins raus. Achtung: fangt auch False Positives (lokale
    /// Vars, Methoden-Namen); fuer den Info-Screen ist die Ober-Auswahl
    /// akzeptabel.</summary>
    private static IEnumerable<string> ExtractIdentifiers(string expr)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in IdentifierPattern.Matches(expr))
        {
            var name = m.Groups[1].Value;
            if (NonVariableTokens.Contains(name)) continue;
            if (seen.Add(name)) yield return name;
        }
    }

    private static void AnalyzeFile(string absPath, string relPath,
        List<RpyChoice> choices, List<RpyStoreVariable> vars,
        HashSet<string> varNamesSeen, List<RpyCharacter> chars,
        Dictionary<string, List<VarConsumer>> consumers,
        List<(string file, int line)> menuLocations,
        List<RpySayStatement> says,
        List<VarDelta> globalDeltas)
    {
        var lines = File.ReadAllLines(absPath);
        var labelLines = BuildLabelMap(lines);
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
                    MenuIndex: menuIndexInLabel,
                    HeaderLine: i + 1));
                menuLocations.Add((relPath, i + 1));
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

                        var deltas = CollectChoiceBodyDeltas(lines, i + 1, indent, labelLines);
                        choices.Add(new RpyChoice(
                            SourceFile: relPath,
                            SourceLine: i + 1,
                            Label: ctx.LabelAtOpen,
                            MenuIndex: ctx.MenuIndex,
                            MenuHeaderLine: ctx.HeaderLine,
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
                var name = mDefault.Groups[1].Value;
                if (varNamesSeen.Add(name))
                {
                    var val = StripLineComment(mDefault.Groups[2].Value).Trim();
                    vars.Add(new RpyStoreVariable(name, val, InferType(val)));
                }
            }
            // $ varname = value → implizite Store-Variable (typisch fuer
            // Story-Vars, die erst beim Betreten eines Labels initialisiert
            // werden statt per `default` im Init-Block). Erstes Vorkommen
            // pro Name gewinnt — sonst wuerde `$ keys = 0` in 5 Labels 5x
            // im Cheat-Menue auftauchen (Interview Desires 0.23).
            else
            {
                var mDollar = DollarInitPattern.Match(line);
                if (mDollar.Success)
                {
                    var name = mDollar.Groups[1].Value;
                    if (varNamesSeen.Add(name))
                    {
                        var val = StripLineComment(mDollar.Groups[2].Value).Trim();
                        vars.Add(new RpyStoreVariable(name, val, InferType(val)));
                    }
                }
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

            // if / elif / while — Variable-Consumer erfassen. Choice-
            // Conditions ("text" if X:) laufen im Nachtrag ueber die
            // choices-Liste — hier nur die Standalone-Conditions.
            var mCond = ConditionPattern.Match(line);
            if (mCond.Success)
            {
                string expr = mCond.Groups[1].Value.Trim();
                foreach (var v in ExtractIdentifiers(expr))
                    AddConsumer(consumers, v, new VarConsumer(
                        relPath, i + 1, currentLabel,
                        VarConsumerKind.Condition, expr));
            }

            // Say-Statement: <character> "text" — nur wenn NICHT in einem
            // menu-Scope (dort waeren wir Choice-Header oder Say im Choice-
            // Body — Choice-Header ist schon oben behandelt, Say-in-Choice
            // sollten wir NICHT als Say erfassen weil sonst duplication).
            // Zusatz-Guard: Zeile beginnt nicht mit einem Ren'Py-Keyword
            // (menu, label, if, elif, else, ...) — dann waere sie kein Say.
            if (menuStack.Count == 0 && !LooksLikeKeywordLine(trimmed))
            {
                var mSay = SayPattern.Match(line);
                if (mSay.Success)
                {
                    string charVar = mSay.Groups[1].Success ? mSay.Groups[1].Value : "";
                    string rawText = mSay.Groups[2].Value;
                    says.Add(new RpySayStatement(relPath, i + 1, charVar, rawText));
                }
            }

            // Globale Delta-Sammlung fuer den Cheat-Generator: ALLE
            // `$ X op Y` und `$ obj.update("attr", val)` im ganzen File
            // (nicht nur in Choice-Bodies). Ohne das fehlen Character-
            // Container-Stats wie fcs.morality im Cheat-Menue, weil die
            // Deltas typischerweise in per-jump-erreichten label-Bodies
            // stehen, nicht direkt unter menu-Choices.
            var mGlobalAssign = DollarAssignPattern.Match(line);
            if (mGlobalAssign.Success)
            {
                string op = mGlobalAssign.Groups[2].Value;
                // Reine `$ X = Y` als "assign" sammeln wir NICHT hier
                // — das sind Initializations, keine Modifikationen die
                // Cheat-worthy waeren (die stehen schon in StoreVariables).
                // Nur compound-ops (+=, -=, *=, /=) sind echte Deltas.
                if (op != "=")
                {
                    globalDeltas.Add(new VarDelta(
                        Variable: mGlobalAssign.Groups[1].Value,
                        Op: op,
                        Value: StripLineComment(mGlobalAssign.Groups[3].Value).Trim()));
                }
            }
            else
            {
                var mGlobalUpd = DollarUpdateCallPattern.Match(line);
                if (mGlobalUpd.Success)
                {
                    string obj = mGlobalUpd.Groups[1].Value;
                    string attr = mGlobalUpd.Groups[2].Value;
                    string val = StripLineComment(mGlobalUpd.Groups[3].Value).Trim();
                    string op = LooksLikeNumericLiteral(val) ? "+=" : "=";
                    globalDeltas.Add(new VarDelta(
                        Variable: $"{obj}.{attr}",
                        Op: op,
                        Value: val));
                }
            }
        }
    }

    private static readonly HashSet<string> SayExclusionKeywords = new(StringComparer.Ordinal)
    {
        "menu", "label", "if", "elif", "else", "while", "for", "with",
        "return", "jump", "call", "scene", "show", "hide", "play", "stop",
        "queue", "pause", "python", "init", "define", "default", "image",
        "transform", "screen", "style", "translate", "$", "voice",
        "window", "nvl", "camera",
    };

    /// <summary>Prueft ob die Zeile mit einem Ren'Py-Keyword beginnt — dann
    /// ist sie kein Say-Statement, auch wenn danach ein String kommt.</summary>
    private static bool LooksLikeKeywordLine(string trimmed)
    {
        int end = 0;
        while (end < trimmed.Length && (char.IsLetterOrDigit(trimmed[end]) || trimmed[end] == '_'))
            end++;
        if (end == 0) return trimmed[0] == '$'; // $-Shortcut
        var firstWord = trimmed[..end];
        return SayExclusionKeywords.Contains(firstWord);
    }

    /// <summary>Sammelt <c>$ var op value</c>-Statements im Body eines
    /// Choices — aber nur die DIREKTEN Deltas, nicht die aus
    /// verschachtelten <c>menu:</c>-Bloecken. Sobald ein inneres
    /// <c>menu:</c> aufgemacht wird, ueberspringen wir alle Zeilen bis
    /// wir wieder auf oder unter dessen Indent-Level zurueck sind.
    ///
    /// **Jump-Follow:** Sieht der Body ein <c>jump X</c> oder <c>call X</c>,
    /// folgen wir in den Body von Label X (nur bekannte Labels im selben
    /// File, Zyklen werden per <c>visitedLabels</c> abgefangen, Tiefe max 3).
    /// Ohne das haetten Choices deren Body NUR aus <c>jump next_scene</c>
    /// besteht (typisch in Boundaries of Morality) keine Deltas und wuerden
    /// im Info-Popup als „no info" durchfallen.</summary>
    private static IReadOnlyList<VarDelta> CollectChoiceBodyDeltas(
        string[] lines, int startIndex, int choiceIndent,
        IReadOnlyDictionary<string, int> labelLines)
    {
        var deltas = new List<VarDelta>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        CollectDeltasFromRange(lines, startIndex, choiceIndent, deltas,
            labelLines, visited, depth: 0);
        return deltas;
    }

    private static void CollectDeltasFromRange(
        string[] lines, int startIndex, int stopIndent,
        List<VarDelta> deltas,
        IReadOnlyDictionary<string, int> labelLines,
        HashSet<string> visitedLabels, int depth)
    {
        // Safety: chain-depth cap gegen Jump-Ketten wie A→B→C.
        // 3 reicht fuer typische „choice → transition-label → scene-label" Ketten.
        const int MaxDepth = 3;
        if (depth > MaxDepth) return;

        int? skipUntilIndent = null;

        for (int i = startIndex; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) continue;
            int indent = line.Length - trimmed.Length;
            if (indent <= stopIndent) break;
            if (trimmed[0] == '#') continue;

            if (skipUntilIndent is int skip)
            {
                if (indent <= skip) skipUntilIndent = null;
                else continue;
            }

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
                continue;
            }
            var mUpd = DollarUpdateCallPattern.Match(line);
            if (mUpd.Success)
            {
                string obj = mUpd.Groups[1].Value;
                string attr = mUpd.Groups[2].Value;
                string val = StripLineComment(mUpd.Groups[3].Value).Trim();
                string op = LooksLikeNumericLiteral(val) ? "+=" : "=";
                deltas.Add(new VarDelta(
                    Variable: $"{obj}.{attr}",
                    Op: op,
                    Value: val));
                continue;
            }

            // jump X / call X → in Ziel-Label reinfolgen und dessen
            // Body-Deltas mitnehmen. Nur bekannte Labels, keine Zyklen.
            var mJump = JumpPattern.Match(line);
            if (mJump.Success)
            {
                string target = mJump.Groups[1].Value;
                if (visitedLabels.Add(target) &&
                    labelLines.TryGetValue(target, out int labelLine))
                {
                    var labelLineText = lines[labelLine];
                    int labelIndent = labelLineText.Length - labelLineText.TrimStart().Length;
                    CollectDeltasFromRange(lines, labelLine + 1, labelIndent,
                        deltas, labelLines, visitedLabels, depth + 1);
                }
            }
        }
    }

    /// <summary>Baut eine Label-Name→Zeilen-Index-Map fuer ein File. Wird
    /// von <see cref="CollectChoiceBodyDeltas"/> gebraucht, um <c>jump X</c>
    /// zum Label-Body zu folgen.</summary>
    private static Dictionary<string, int> BuildLabelMap(string[] lines)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < lines.Length; i++)
        {
            var m = LabelPattern.Match(lines[i]);
            if (m.Success) map[m.Groups[1].Value] = i;
        }
        return map;
    }

    private static bool LooksLikeNumericLiteral(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return false;
        if (s[0] == '-' || s[0] == '+') s = s[1..];
        return s.Length > 0 && s.All(c => char.IsDigit(c) || c == '.');
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
        string LabelAtOpen, int MenuIndex, int HeaderLine);
}
