using System.Text;
using NLog;

namespace RenPack.Services.Modding;

/// <summary>
/// Erzeugt einen KrosteMod-Walkthrough aus einer <see cref="ModAnalysis"/>:
/// jeder <c>menu:</c>-Choice bekommt einen farbigen Suffix mit den
/// erkannten Wirkungen (<c>[K love+3]</c>, <c>[K respect-1]</c>,
/// <c>[K flag_set]</c>), damit der Spieler beim Spielen sieht was
/// welche Antwort bewirkt.
///
/// **Was wird ausgegeben?** Fuer jede .rpy-Datei mit mindestens einem
/// Choice wird eine gepatchte Kopie im Ziel-Ordner erzeugt. Der Nutzer
/// spielt die Dateien in den <c>game/</c>-Ordner seines Ren'Py-Spiels
/// ein und ueberschreibt die Originale (Backup empfohlen).
///
/// **Warum patchen wir die Originale statt Overlay?** Ren'Py kennt keine
/// einfache Label-Override-Semantik — ein zweites <c>label start:</c>
/// wuerde einen Compile-Fehler geben. Overrides via <c>init offset</c>
/// funktionieren nur fuer <c>define</c>/<c>default</c>, nicht fuer
/// Labels/Menus. Also patchen wir die Zeilen direkt und der Nutzer
/// kopiert die geaenderten Files ins Spiel.
/// </summary>
public sealed class KrosteWalkthroughGenerator
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Farbe fuer die Hint-Suffixes im Choice-Text. Kroste-Gold,
    /// damit die Hinweise sich klar vom Ren'Py-Choice-Text abheben.</summary>
    private const string HintColor = "#e0b14c";

    /// <summary>Baut den Walkthrough-Mod. <paramref name="sourceRoot"/>
    /// ist ein dekompilierter Spiel-Ordner (typischerweise das
    /// <c>game/</c>-Verzeichnis), <paramref name="destRoot"/> das Ziel
    /// (wird angelegt, existierende Files werden ueberschrieben).
    ///
    /// <paramref name="gameRootWithTl"/>: optional der Original-<c>game/</c>-
    /// Ordner. Wenn dort <c>tl/&lt;lang&gt;/</c>-Unterordner existieren
    /// (das Spiel hat Uebersetzungen), schaltet der Generator in den
    /// Translation-Aware Mode: Choice-Text bleibt unmodifiziert (sonst
    /// wuerde Ren'Py's Translation-Lookup den gepatched String nicht mehr
    /// finden und die deutsche Uebersetzung wuerde greifen — OHNE unseren
    /// Hint). Stattdessen schreiben wir <c>tl/&lt;lang&gt;/krostemod_walkthrough_hints.rpy</c>
    /// pro Language mit <c>translate &lt;lang&gt; strings:</c>-Blocks die
    /// den Original-Text auf Original+Hint mappen.</summary>
    /// <returns>Anzahl geschriebener .rpy-Dateien.</returns>
    public int Generate(string sourceRoot, string destRoot, ModAnalysis analysis,
        string? gameRootWithTl = null)
    {
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Source-Root nicht gefunden: {sourceRoot}");
        Directory.CreateDirectory(destRoot);

        // Translation-Aware Mode: erkennen wenn das Spiel tl/-Ordner hat.
        // Wir nutzen gameRootWithTl (das echte gameDir des Spiels) — der
        // sourceRoot ist meist ein Decompile-Temp-Ordner ohne tl/.
        var tlLanguages = DetectTranslationLanguages(gameRootWithTl ?? sourceRoot);
        bool translationAware = tlLanguages.Count > 0;
        if (translationAware)
            Log.Info("Translation-Aware Mode: {count} tl-Language(s) gefunden ({langs}) — " +
                "Choice-Text bleibt unmodifiziert, Hints via translate-strings-Files",
                tlLanguages.Count, string.Join(", ", tlLanguages));

        // Choices pro Datei bundeln (Dictionary: file → List<Choice>),
        // damit wir nur die relevanten Dateien anfassen.
        var byFile = analysis.Choices
            .GroupBy(c => c.SourceFile, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // Fuer Translation-Aware: (originalText, hintSuffix)-Paare sammeln
        // die dann in die tl/-Files geschrieben werden.
        var hintsForTranslation = new List<(string OriginalText, string Hint)>();

        int written = 0;
        foreach (var (relPath, choices) in byFile)
        {
            var srcAbs = Path.Combine(sourceRoot, relPath);
            var dstAbs = Path.Combine(destRoot, relPath);
            if (!File.Exists(srcAbs))
            {
                Log.Warn("Skip {file}: Quelle nicht vorhanden (Analyse-Root ≠ Source-Root?)", relPath);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dstAbs)!);

            var lines = File.ReadAllLines(srcAbs);
            var choicesByLine = choices
                .GroupBy(c => c.SourceLine)
                .ToDictionary(g => g.Key, g => g.First());

            var output = new List<string>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                if (choicesByLine.TryGetValue(i + 1, out var choice))
                {
                    if (translationAware)
                    {
                        // Choice-Text unmodifiziert lassen, aber (Original, Hint)
                        // fuer die tl/-Files sammeln.
                        var hint = FormatHint(choice);
                        if (!string.IsNullOrEmpty(hint))
                        {
                            var original = ExtractQuotedText(lines[i]);
                            if (original is not null)
                                hintsForTranslation.Add((original, hint));
                        }
                        output.Add(lines[i]);
                    }
                    else
                    {
                        output.Add(PatchChoiceLine(lines[i], choice));
                    }
                }
                else
                    output.Add(lines[i]);
            }

            File.WriteAllText(dstAbs, string.Join('\n', output));
            written++;
        }

        // Translation-Runtime-Patcher schreiben (EINE Datei fuer alle Sprachen).
        if (translationAware && hintsForTranslation.Count > 0)
        {
            WriteTranslationHintPatcher(destRoot, tlLanguages, hintsForTranslation);
            Log.Info("Translation-Aware: {count} Hints via Runtime-Patcher fuer {langs} Sprache(n)",
                hintsForTranslation.Count, tlLanguages.Count);
        }

        // README als Hinweis fuer den Nutzer.
        WriteReadme(destRoot, written, analysis, translationAware, tlLanguages);

        Log.Info("KrosteMod-Walkthrough erzeugt: {count} .rpy-Datei(en) → {dest}",
            written, destRoot);
        return written;
    }

    /// <summary>Listet die Sprachen im <c>game/tl/</c>-Ordner. Ausgeschlossen
    /// werden Ren'Py-Steuersubordner wie <c>None</c> (default-Sprache-Placeholder).</summary>
    private static IReadOnlyList<string> DetectTranslationLanguages(string gameDir)
    {
        var tlDir = Path.Combine(gameDir, "tl");
        if (!Directory.Exists(tlDir)) return Array.Empty<string>();
        var result = new List<string>();
        foreach (var sub in Directory.EnumerateDirectories(tlDir))
        {
            var name = Path.GetFileName(sub);
            // "None" = default-Sprache-Placeholder von Ren'Py; kein echter tl-Ordner.
            if (name is "None" or "none") continue;
            // Muss mind. eine .rpy oder .rpyc-Datei enthalten sonst ist's kein
            // funktionierender Language-Folder.
            if (Directory.EnumerateFiles(sub, "*.rpy*").Any())
                result.Add(name);
        }
        return result;
    }

    /// <summary>Extrahiert den String zwischen den ersten " " einer Zeile —
    /// respektiert Escape-Sequenzen (\").</summary>
    private static string? ExtractQuotedText(string line)
    {
        int start = line.IndexOf('"');
        if (start < 0) return null;
        int end = FindClosingQuote(line, start + 1);
        if (end < 0) return null;
        return line.Substring(start + 1, end - start - 1);
    }

    /// <summary>Schreibt EINE <c>krostemod_walkthrough_hints.rpy</c> im
    /// game/-Root mit einem <c>init 999 python:</c>-Block der zur Runtime
    /// das Translation-Dictionary jeder tl-Language direkt patcht.
    ///
    /// Warum nicht AST-basiertes <c>translate &lt;lang&gt; strings:</c>? Ren'Py
    /// wirft <c>Exception('A translation for "..." already exists at ...')</c>
    /// bei Duplikaten — und Boundaries of Morality hat fuer viele Choice-
    /// Strings bereits eine Uebersetzung. Verifiziert an traceback.txt vom
    /// User (v0.9.1-Bug).
    ///
    /// Der Runtime-Patcher umgeht die Duplikat-Pruefung, indem er direkt
    /// ins <c>translator.strings[lang].translations</c>-Dict schreibt.
    /// UEBERSCHREIBT bestehende Uebersetzungen — genau das was wir wollen:
    /// unsere Hint-Version soll gewinnen. Falls unser Original-String nicht
    /// exakt matcht (weil Ren'Py's Bytecode einen anderen Text hat), passiert
    /// nichts (silent no-op).</summary>
    private static void WriteTranslationHintPatcher(string destRoot,
        IReadOnlyList<string> tlLanguages,
        IReadOnlyList<(string OriginalText, string Hint)> hints)
    {
        // Dedup nach Original-Text (der Hint ist deterministisch pro Text).
        var deduped = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (orig, hint) in hints)
            if (!deduped.ContainsKey(orig)) deduped[orig] = hint;

        var filePath = Path.Combine(destRoot, "krostemod_walkthrough_hints.rpy");
        var sb = new StringBuilder();
        sb.AppendLine("# =====================================================================");
        sb.AppendLine("# KrosteMod — Walkthrough-Hints (Translation-Aware Runtime-Patcher)");
        sb.AppendLine($"# {deduped.Count} unique Choice-Strings, {tlLanguages.Count} Zielsprachen: {string.Join(", ", tlLanguages)}");
        sb.AppendLine("# Autogeneriert von RenPack — bitte nicht manuell editieren.");
        sb.AppendLine("# =====================================================================");
        sb.AppendLine();
        // Late init damit alle tl-Files bereits geladen sind. Wir muessen
        // NACH dem Original-Translation-Load kommen, sonst ueberschreibt
        // das Original unsere Patches.
        sb.AppendLine("init 999 python:");
        sb.AppendLine();
        sb.AppendLine("    krostemod_walkthrough_langs = " + FormatPythonStringList(tlLanguages));
        sb.AppendLine();
        sb.AppendLine("    krostemod_walkthrough_hints = {");
        foreach (var (orig, hint) in deduped)
        {
            sb.Append("        ")
              .Append(FormatPythonString(orig))
              .Append(": ")
              .Append(FormatPythonString(orig + " " + hint))
              .AppendLine(",");
        }
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    def _krostemod_apply_walkthrough_hints(*_args, **_kw):");
        sb.AppendLine("        # Ren'Py's ScriptTranslator-Instanz liegt unter");
        sb.AppendLine("        # renpy.game.script.translator (NICHT direkt in");
        sb.AppendLine("        # renpy.translation — dort ist nur die Klasse).");
        sb.AppendLine("        try:");
        sb.AppendLine("            strings = renpy.game.script.translator.strings");
        sb.AppendLine("        except Exception as e:");
        sb.AppendLine("            renpy.notify('krostemod: kein Translator-Zugriff: ' + str(e))");
        sb.AppendLine("            return");
        sb.AppendLine("        patched = 0");
        sb.AppendLine("        langs_found = []");
        sb.AppendLine("        for lang in krostemod_walkthrough_langs:");
        sb.AppendLine("            stl = strings.get(lang)");
        sb.AppendLine("            if stl is None: continue");
        sb.AppendLine("            translations = getattr(stl, 'translations', None)");
        sb.AppendLine("            if translations is None: continue");
        sb.AppendLine("            langs_found.append(lang)");
        sb.AppendLine("            for old, new in krostemod_walkthrough_hints.items():");
        sb.AppendLine("                translations[old] = new");
        sb.AppendLine("                patched += 1");
        sb.AppendLine("        # Log ins Ren'Py-Log damit sichtbar ist ob was gemacht wurde.");
        sb.AppendLine("        # renpy.log() geht in log.txt (Debug), plus notify auf Screen.");
        sb.AppendLine("        try: renpy.log('krostemod walkthrough: patched {} entries across {} langs ({})'.format(");
        sb.AppendLine("            patched, len(langs_found), ','.join(langs_found)))");
        sb.AppendLine("        except Exception: pass");
        sb.AppendLine();
        sb.AppendLine("    # Sofort einmal beim Init-Ende ausfuehren.");
        sb.AppendLine("    _krostemod_apply_walkthrough_hints()");
        sb.AppendLine();
        sb.AppendLine("    # UND bei jedem Sprach-Wechsel neu — Ren'Py wechselt");
        sb.AppendLine("    # translations-Dict dynamisch beim `renpy.change_language()`-");
        sb.AppendLine("    # Aufruf. Ohne diese Hook waere unser Patch nach dem ersten");
        sb.AppendLine("    # Sprach-Change weg.");
        sb.AppendLine("    if hasattr(config, 'change_language_callbacks'):");
        sb.AppendLine("        if _krostemod_apply_walkthrough_hints not in config.change_language_callbacks:");
        sb.AppendLine("            config.change_language_callbacks.append(_krostemod_apply_walkthrough_hints)");
        File.WriteAllText(filePath, sb.ToString(), new System.Text.UTF8Encoding(false));
        Log.Info("Translation-Hint-Patcher geschrieben: {path} ({count} Strings x {langs} Sprachen)",
            filePath, deduped.Count, tlLanguages.Count);
    }

    /// <summary>Python-String-Literal mit Escape-Handling fuer alle
    /// problematischen Zeichen (Quotes, Backslash, Newlines, Unicode).
    /// Wir nutzen ALWAYS Double-Quotes und escapen entsprechend.</summary>
    private static string FormatPythonString(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string FormatPythonStringList(IEnumerable<string> items)
    {
        return "[" + string.Join(", ", items.Select(FormatPythonString)) + "]";
    }

    /// <summary>Fuegt in eine Choice-Header-Zeile den Hint-Suffix ein.
    /// Wir modifizieren nur den String zwischen den Anfuehrungszeichen
    /// (die syntaktische Umgebung — Einrueckung, <c>":"</c>, evtl.
    /// <c>if</c>-Condition — bleibt unveraendert).</summary>
    private static string PatchChoiceLine(string originalLine, RpyChoice choice)
    {
        var hint = FormatHint(choice);
        if (string.IsNullOrEmpty(hint)) return originalLine;

        // Finde die schliessende " des Choice-Textes. Wir muessen
        // Escape-Sequenzen (\") respektieren — sonst schliessen wir zu
        // frueh.
        int start = originalLine.IndexOf('"');
        if (start < 0) return originalLine;
        int end = FindClosingQuote(originalLine, start + 1);
        if (end < 0) return originalLine;

        // Suffix VOR dem schliessenden " einfuegen. Die Ren'Py-Farb-
        // Formatierung ({color=…}…{/color}) klappt in Choice-Texten.
        return originalLine[..end] + " " + hint + originalLine[end..];
    }

    private static int FindClosingQuote(string line, int from)
    {
        for (int i = from; i < line.Length; i++)
        {
            if (line[i] == '\\' && i + 1 < line.Length) { i++; continue; }
            if (line[i] == '"') return i;
        }
        return -1;
    }

    /// <summary>Erzeugt den Hint-String aus den Deltas eines Choices.
    /// Format: <c>{color=#e0b14c}(K var+N) (K other-M) (K flag_set){/color}</c>.
    /// Leerer String, wenn keine erkennbaren Deltas existieren (dann
    /// bleibt der Original-Choice-Text unveraendert).
    ///
    /// **Warum runde Klammern statt <c>[…]</c>?** Ren'Py interpretiert
    /// <c>[…]</c> in Text-Strings als Python-Interpolation. Die
    /// Native-Escape-Sequenz <c>[[</c> loest die aeussere Runde auf, aber
    /// viele Spiele haben custom <c>screens.rpy</c>, die den Choice-Text
    /// NOCHMAL durch die Substitution jagen (verifiziert an Sophia Parker
    /// 0.230, wo <c>[[K filthy+1]</c> im deployten Mod nach der ersten
    /// Substitution zu <c>[K filthy+1]</c> wurde und beim zweiten Durchlauf
    /// dann als Python-Expression versucht wurde → <c>SyntaxError</c>).
    /// Runde Klammern haben keine Sonderbedeutung in Ren'Py-Text und ueber-
    /// leben beliebig viele Substitutions-Runden.</summary>
    public static string FormatHint(RpyChoice choice)
    {
        if (choice.Deltas.Count == 0) return "";
        var parts = new List<string>(choice.Deltas.Count);
        foreach (var d in choice.Deltas)
        {
            var tag = FormatDelta(d);
            if (tag is not null) parts.Add($"(K {tag})");
        }
        if (parts.Count == 0) return "";
        return "{color=" + HintColor + "}" + string.Join(' ', parts) + "{/color}";
    }

    private static string? FormatDelta(VarDelta d)
    {
        // Nur einfache Zahl-Deltas als Vorzeichen-Notation. Alles andere
        // (String-Zuweisungen, komplexe Ausdruecke) wird als "flag_set"
        // dargestellt — reicht dem Spieler als "es ist etwas passiert".
        switch (d.Op)
        {
            case "+=":
                if (IsNumeric(d.Value)) return $"{d.Variable}+{d.Value}";
                return $"{d.Variable} set";
            case "-=":
                if (IsNumeric(d.Value)) return $"{d.Variable}-{d.Value}";
                return $"{d.Variable} set";
            case "=":
                if (d.Value == "True") return $"{d.Variable} set";
                if (d.Value == "False") return $"{d.Variable} clear";
                if (IsNumeric(d.Value)) return $"{d.Variable}={d.Value}";
                return $"{d.Variable} set";
            case "*=" or "/=":
                return $"{d.Variable} {d.Op} {d.Value}";
            default:
                return null;
        }
    }

    private static bool IsNumeric(string s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _);

    private static void WriteReadme(string destRoot, int fileCount, ModAnalysis analysis,
        bool translationAware, IReadOnlyList<string> tlLanguages)
    {
        var choiceCount = analysis.Choices.Count;
        var sb = new StringBuilder();
        sb.AppendLine("# KrosteMod — Walkthrough");
        sb.AppendLine();
        sb.AppendLine("Automatisch generiert von RenPack. Fuegt bei jedem Choice ");
        sb.AppendLine("(`menu:`-Auswahl) einen goldenen Hinweis-Tag ein, der zeigt,");
        sb.AppendLine("welche Store-Variablen der Choice veraendert.");
        sb.AppendLine();
        sb.AppendLine($"Statistik:");
        sb.AppendLine($"- Patched files: {fileCount}");
        sb.AppendLine($"- Choices annotated: {choiceCount}");
        sb.AppendLine($"- Store variables discovered: {analysis.StoreVariables.Count}");
        if (translationAware)
        {
            sb.AppendLine();
            sb.AppendLine("**Translation-Aware Mode aktiv**");
            sb.AppendLine();
            sb.AppendLine($"Das Spiel hat Uebersetzungen fuer: {string.Join(", ", tlLanguages)}.");
            sb.AppendLine("Damit die Hints in JEDER Sprache sichtbar sind, wurden pro Sprache");
            sb.AppendLine("`tl/<lang>/krostemod_walkthrough_hints.rpy`-Dateien erzeugt. Diese");
            sb.AppendLine("registrieren fuer jeden Choice einen Uebersetzungs-Ersatz mit Hint.");
            sb.AppendLine("Trade-off: wenn das Spiel eine echte Uebersetzung fuer denselben");
            sb.AppendLine("Choice hat, gewinnt die zuletzt geladene Definition — im Zweifel");
            sb.AppendLine("bleibt der Original-Text (mit Hint) statt der Uebersetzung.");
        }
        sb.AppendLine();
        sb.AppendLine("## Installation");
        sb.AppendLine();
        sb.AppendLine("1. Backup des Spiel-`game/`-Ordners machen.");
        sb.AppendLine("2. Die Dateien aus diesem Ordner in das Spiel-`game/`-Verzeichnis kopieren.");
        sb.AppendLine("3. Spiel starten — Hinweise erscheinen als `(K love+3)` etc. in den Menu-Optionen.");
        sb.AppendLine();
        sb.AppendLine("## Deinstallation");
        sb.AppendLine();
        sb.AppendLine("Backup aus Schritt 1 zurueckspielen.");
        File.WriteAllText(Path.Combine(destRoot, "KROSTEMOD_README.md"), sb.ToString());
    }
}
