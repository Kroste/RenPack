using System.Text;
using NLog;

namespace RenPack.Services.Modding;

/// <summary>
/// Erzeugt eine <c>krostemod_rename.rpy</c>, die Ren'Py-Character-Objekte
/// umbenennt. Der Nutzer waehlt aus der vom Analyzer erkannten Character-
/// Liste (siehe <see cref="RpyCharacter"/>) welche Character einen neuen
/// Display-Namen bekommen sollen.
///
/// **Wie funktioniert der Rename?** Statt die Original-.rpy-Dateien zu
/// patchen (fragil, kann Story-Referenzen zerreissen), setzen wir mit
/// hoher <c>init</c>-Prioritaet die <c>.name</c>-Attribute der Character-
/// Objekte direkt:
/// <code>
/// init 1000 python:
///     try: store.Sophia.name = "Anna"
///     except Exception: pass
/// </code>
/// Ren'Py's <c>Character</c>-Klasse traegt das <c>name</c>-Feld als
/// gerenderten Namen — Say-Statements wie <c>sophia "Hallo"</c> zeigen
/// dann den neuen Namen an. Der Python-Identifier <c>Sophia</c> bleibt
/// erhalten, damit alle Say-Statements im Original weiter funktionieren.
///
/// **Was wird NICHT geaendert?** Body-Text der Dialoge (z.B.
/// <c>"Hi Sophia, wie geht's?"</c>) bleibt unangetastet — das koennte
/// KI-basiert konsistent umgeschrieben werden (E4b, spaeter), ist aber
/// riskant und nicht Teil des MVP.
/// </summary>
public sealed class KrosteRenameGenerator
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Schreibt die <c>krostemod_rename.rpy</c> nach
    /// <paramref name="destDir"/>. Nur Character die eine Mapping haben
    /// (non-empty NewName) werden emittiert.
    ///
    /// Wenn <paramref name="decompiledSourceRoot"/> gesetzt UND
    /// <c>config.BodyTextEdits</c> non-empty ist, patcht der Generator
    /// zusaetzlich die betroffenen .rpy-Dateien: er kopiert die decompi-
    /// lierten Originale aus <paramref name="decompiledSourceRoot"/> in
    /// den <paramref name="destDir"/> mit der gleichen relativen Ordner-
    /// struktur, wendet die Edits an, und der normale Deploy-Loop
    /// (OneClickModBuilder) kopiert sie ins game/.</summary>
    public string Generate(string destDir, ModAnalysis analysis, RenameConfig config,
        string? decompiledSourceRoot = null)
    {
        Directory.CreateDirectory(destDir);
        var target = Path.Combine(destDir, "krostemod_rename.rpy");

        // Mappings validieren: nur non-leere Ziel-Namen zaehlen, und der
        // Character muss auch analytisch bekannt sein (sonst schreiben wir
        // fuer Ghost-Namen).
        var known = new HashSet<string>(
            analysis.Characters.Select(c => c.VarName), StringComparer.Ordinal);
        var effective = config.Mappings
            .Where(kv => known.Contains(kv.Key)
                         && !string.IsNullOrWhiteSpace(kv.Value)
                         && kv.Value.Trim() != analysis.Characters
                             .First(c => c.VarName == kv.Key).DisplayName)
            .ToList();

        var sb = new StringBuilder();
        WriteHeader(sb, effective.Count);
        WriteRenameBlock(sb, effective, analysis);

        File.WriteAllText(target, sb.ToString());
        Log.Info("KrosteMod-Rename erzeugt: {path} ({count} Character umbenannt)",
            target, effective.Count);

        // Body-Text-Patches (E4b) — nur wenn Source-Root gegeben und
        // Edits akzeptiert sind. Der Patcher schreibt jede angefasste .rpy
        // mit den Ersetzungen in den destDir; der Deploy-Loop im
        // OneClickModBuilder nimmt sie mit ins game/.
        var acceptedEdits = config.BodyTextEdits?
            .Where(e => e.Accepted).ToList() ?? [];
        if (decompiledSourceRoot is not null && acceptedEdits.Count > 0)
        {
            int patched = ApplyBodyTextEdits(decompiledSourceRoot, destDir, acceptedEdits);
            Log.Info("KrosteMod-Rename Body-Text: {patched} .rpy-Datei(en) gepatcht", patched);
        }

        return target;
    }

    /// <summary>Wendet die Body-Text-Edits an: pro betroffener Datei die
    /// dekompilierte Original-Version kopieren, die Zeilen ersetzen,
    /// und die Kopie in <paramref name="destDir"/> ablegen. Rueckgabe ist
    /// die Anzahl geaenderter Dateien.</summary>
    private static int ApplyBodyTextEdits(string sourceRoot, string destDir,
        IReadOnlyList<BodyTextEdit> edits)
    {
        var byFile = edits
            .GroupBy(e => e.SourceFile, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        int patchedCount = 0;
        foreach (var (relPath, fileEdits) in byFile)
        {
            var srcAbs = Path.Combine(sourceRoot, relPath);
            var dstAbs = Path.Combine(destDir, relPath);
            if (!File.Exists(srcAbs))
            {
                Log.Warn("Body-Text-Patch skip {file}: Source nicht gefunden", relPath);
                continue;
            }

            var lines = File.ReadAllLines(srcAbs);
            var editsByLine = fileEdits
                .GroupBy(e => e.SourceLine)
                .ToDictionary(g => g.Key, g => g.First());

            bool anyChange = false;
            for (int i = 0; i < lines.Length; i++)
            {
                if (!editsByLine.TryGetValue(i + 1, out var edit)) continue;
                var patched = ReplaceInLine(lines[i], edit.OriginalText, edit.NewText);
                if (patched is null)
                {
                    Log.Warn("Body-Text-Patch skip {file}:{line}: OriginalText nicht gefunden ({orig})",
                        relPath, i + 1, edit.OriginalText);
                    continue;
                }
                lines[i] = patched;
                anyChange = true;
            }
            if (!anyChange) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(dstAbs)!);
            File.WriteAllText(dstAbs, string.Join('\n', lines));
            patchedCount++;
        }
        return patchedCount;
    }

    /// <summary>Ersetzt in <paramref name="line"/> das erste Vorkommen von
    /// <paramref name="original"/> (in Anfuehrungszeichen) durch
    /// <paramref name="replacement"/>. Rueckgabe <c>null</c> wenn nicht
    /// gefunden — dann bleibt die Zeile unveraendert und der Aufrufer
    /// loggt eine Warnung. Wir suchen den Text zwischen <c>"..."</c> weil
    /// nur DAS der Body-Text ist (Character-Var davor bleibt unangetastet).</summary>
    public static string? ReplaceInLine(string line, string original, string replacement)
    {
        var quoted = "\"" + original + "\"";
        int idx = line.IndexOf(quoted, StringComparison.Ordinal);
        if (idx < 0) return null;
        return line[..idx] + "\"" + replacement + "\"" + line[(idx + quoted.Length)..];
    }

    private static void WriteHeader(StringBuilder sb, int count)
    {
        sb.AppendLine("# =====================================================================");
        sb.AppendLine("# KrosteMod — Character Rename");
        sb.AppendLine("# Automatisch erzeugt von RenPack. Ueberschreibt die Anzeige-Namen");
        sb.AppendLine($"# von {count} Character-Objekt(en) via .name-Attribut-Mutation.");
        sb.AppendLine("# Der Python-Identifier (z.B. Sophia) bleibt unveraendert — nur");
        sb.AppendLine("# was Ren'Py als Sprecher-Namen rendert.");
        sb.AppendLine("# =====================================================================");
        sb.AppendLine();
    }

    private static void WriteRenameBlock(StringBuilder sb,
        IReadOnlyList<KeyValuePair<string, string>> mappings,
        ModAnalysis analysis)
    {
        // Original-Namen fuer Kommentar-Zeile lookuppen (dokumentiert
        // den Uninstall-Zustand).
        var originalByName = analysis.Characters
            .ToDictionary(c => c.VarName, c => c.DisplayName, StringComparer.Ordinal);

        sb.AppendLine("init 1000 python:");
        if (mappings.Count == 0)
        {
            sb.AppendLine("    # (keine effektiven Mappings — leerer Rename-Mod)");
            sb.AppendLine("    pass");
            return;
        }
        foreach (var (varName, newName) in mappings)
        {
            var oldName = originalByName.GetValueOrDefault(varName, "?");
            sb.AppendLine($"    # {varName}: \"{oldName}\" → \"{newName}\"");
            sb.AppendLine($"    try: store.{varName}.name = {PyStr(newName.Trim())}");
            sb.AppendLine($"    except Exception as _e: renpy.log('krostemod rename fail ({varName}): ' + str(_e))");
        }
    }

    private static string PyStr(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append($"\\x{(int)c:x2}");
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}

/// <summary>Config-Objekt fuer den Rename-Mod-Build. <see cref="Mappings"/>
/// mapped Character-VarName (aus <see cref="RpyCharacter.VarName"/>) auf
/// den neuen Display-Namen. Leere Werte werden vom Generator ignoriert
/// (kein Rename fuer diesen Character).
///
/// <see cref="BodyTextEdits"/> (E4b, optional): die vom KI-Rewriter
/// vorgeschlagenen und vom User akzeptierten Body-Text-Ersetzungen. Wenn
/// null oder leer, bleibt der Original-Dialog-Text unangetastet — es
/// wird nur der Anzeige-Name des Character-Objekts umbenannt.
///
/// <see cref="RelationMappings"/> (E4c, optional): freie Wort-zu-Wort-
/// Mappings, z.B. <c>mother→aunt</c>, <c>son→nephew</c>. Wird zusammen
/// mit den Character-Mappings an den KI-Rewriter uebergeben — die KI
/// schreibt Body-Texte konsistent um, inkl. Grammatik (Genitiv, Possessiv,
/// deutsche Faelle). Betrifft NUR Body-Text; Character-Objekt-Namen bleiben
/// unveraendert wenn kein zusaetzliches Character-Mapping da ist.</summary>
public sealed record RenameConfig(
    IReadOnlyDictionary<string, string> Mappings,
    IReadOnlyList<BodyTextEdit>? BodyTextEdits = null,
    IReadOnlyDictionary<string, string>? RelationMappings = null);
