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
    /// (wird angelegt, existierende Files werden ueberschrieben).</summary>
    /// <returns>Anzahl geschriebener .rpy-Dateien.</returns>
    public int Generate(string sourceRoot, string destRoot, ModAnalysis analysis)
    {
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Source-Root nicht gefunden: {sourceRoot}");
        Directory.CreateDirectory(destRoot);

        // Choices pro Datei bundeln (Dictionary: file → List<Choice>),
        // damit wir nur die relevanten Dateien anfassen.
        var byFile = analysis.Choices
            .GroupBy(c => c.SourceFile, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

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
            // Choice-Line → Choice-Objekt zum schnellen Nachschlagen.
            // Bei mehreren Choices in derselben Zeile (theoretisch) nehmen
            // wir den ersten — das kommt in der Praxis nicht vor.
            var choicesByLine = choices
                .GroupBy(c => c.SourceLine)
                .ToDictionary(g => g.Key, g => g.First());

            var output = new List<string>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                if (choicesByLine.TryGetValue(i + 1, out var choice))
                    output.Add(PatchChoiceLine(lines[i], choice));
                else
                    output.Add(lines[i]);
            }

            File.WriteAllText(dstAbs, string.Join('\n', output));
            written++;
        }

        // README als Hinweis fuer den Nutzer.
        WriteReadme(destRoot, written, analysis);

        Log.Info("KrosteMod-Walkthrough erzeugt: {count} .rpy-Datei(en) → {dest}",
            written, destRoot);
        return written;
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

    private static void WriteReadme(string destRoot, int fileCount, ModAnalysis analysis)
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
        sb.AppendLine();
        sb.AppendLine("## Installation");
        sb.AppendLine();
        sb.AppendLine("1. Backup des Spiel-`game/`-Ordners machen.");
        sb.AppendLine("2. Die Dateien aus diesem Ordner in das Spiel-`game/`-Verzeichnis kopieren.");
        sb.AppendLine("3. Spiel starten — Hinweise erscheinen als `[K love+3]` etc. in den Menu-Optionen.");
        sb.AppendLine();
        sb.AppendLine("## Deinstallation");
        sb.AppendLine();
        sb.AppendLine("Backup aus Schritt 1 zurueckspielen.");
        File.WriteAllText(Path.Combine(destRoot, "KROSTEMOD_README.md"), sb.ToString());
    }
}
