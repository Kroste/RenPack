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

        // Translation-Files pro Language schreiben.
        if (translationAware && hintsForTranslation.Count > 0)
        {
            foreach (var lang in tlLanguages)
                WriteTranslationHintFile(destRoot, lang, hintsForTranslation);
            Log.Info("Translation-Aware: {count} Hints in {langs} Sprache(n) via tl/-Files",
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

    /// <summary>Schreibt eine krostemod_walkthrough_hints.rpy pro tl-Language
    /// mit <c>translate &lt;lang&gt; strings:</c>-Blocks. Jeder Block mappt
    /// den unmodifizierten Original-Choice-Text auf den Hint-erweiterten Text.
    ///
    /// Trade-off: wenn das Spiel eine echte Uebersetzung fuer denselben Original-
    /// String hat, gewinnt die zuletzt geladene Definition (Ren'Py-Load-Order).
    /// Wir landen im Zweifel spaeter (unser File beginnt mit "krostemod_",
    /// alphabetisch typisch weit hinten) → Hint sichtbar aber Original-Sprache
    /// statt Uebersetzung. Fuer den User im Screenshot ist das Verbesserung:
    /// vorher zeigte Ren'Py entweder Original (ohne Hint) oder Uebersetzung
    /// (ohne Hint) — mit uns: Original + Hint.</summary>
    private static void WriteTranslationHintFile(string destRoot, string language,
        IReadOnlyList<(string OriginalText, string Hint)> hints)
    {
        var relDir = Path.Combine("tl", language);
        var absDir = Path.Combine(destRoot, relDir);
        Directory.CreateDirectory(absDir);
        var filePath = Path.Combine(absDir, "krostemod_walkthrough_hints.rpy");

        var sb = new StringBuilder();
        sb.AppendLine("# =====================================================================");
        sb.AppendLine($"# KrosteMod — Walkthrough-Hints fuer {language}");
        sb.AppendLine("# Ergaenzt Choice-Texte um goldene Hint-Suffixes (K var+N).");
        sb.AppendLine("# Autogeneriert von RenPack — bitte nicht manuell editieren.");
        sb.AppendLine("# =====================================================================");
        sb.AppendLine();
        sb.AppendLine($"translate {language} strings:");
        sb.AppendLine();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (originalText, hint) in hints)
        {
            if (!seen.Add(originalText)) continue; // Dedup
            sb.Append("    old \"").Append(EscapeForRenpy(originalText)).AppendLine("\"");
            sb.Append("    new \"").Append(EscapeForRenpy(originalText)).Append(" ").Append(EscapeForRenpy(hint)).AppendLine("\"");
            sb.AppendLine();
        }
        File.WriteAllText(filePath, sb.ToString(), new System.Text.UTF8Encoding(false));
        Log.Info("Translation-Hints geschrieben: {path} ({count} Strings)", filePath, seen.Count);
    }

    private static string EscapeForRenpy(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\r': break;
                case '\n': sb.Append("\\n"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
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
