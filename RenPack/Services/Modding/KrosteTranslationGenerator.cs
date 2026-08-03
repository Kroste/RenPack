using System.Text;
using NLog;

namespace RenPack.Services.Modding;

/// <summary>
/// Schreibt Ren'Py-Translation-Files fuer den KrosteMod-Translation-Mod (E6).
/// Pro Zielsprache eine Datei <c>game/tl/&lt;lang&gt;/krostemod_translations.rpy</c>
/// mit einem <c>translate &lt;lang&gt; strings:</c>-Block der alle uebersetzten
/// Strings als <c>old "..." / new "..."</c>-Paare enthaelt.
///
/// Ren'Py's Translation-System matcht die <c>old</c>-Strings gegen alle Say-
/// und Menu-Texte im Spiel — bei Match wird stattdessen der <c>new</c>-String
/// gerendert. Der User waehlt im Preferences-Menue seine Sprache
/// (Standard-Ren'Py-Feature; Language-Selector erscheint automatisch sobald
/// mind. eine tl/-Sprache existiert).
///
/// **Vorteile gegenueber Body-Rewrite (E4b/c):**
/// - Original bleibt unangetastet — User kann jederzeit zurueckwechseln
/// - Ren'Py-nativ, keine Duplicate-Label-Konflikte
/// - Wenn der Autor spaeter selbst Sprach-Support nachliefert, laesst sich
///   der Mod deinstallieren und die offizielle Uebersetzung uebernimmt.
/// </summary>
public sealed class KrosteTranslationGenerator
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Schreibt fuer jede Zielsprache mit non-leeren Uebersetzungen
    /// eine .rpy-Datei nach <paramref name="destDir"/>. Struktur:
    /// <c>destDir/tl/&lt;renpy-lang-code&gt;/krostemod_translations.rpy</c>.
    /// Zurueckgegeben werden die geschriebenen Datei-Pfade (relativ zu
    /// <paramref name="destDir"/>) — der Deploy-Loop kopiert sie ins game/.</summary>
    public IReadOnlyList<string> Generate(string destDir, TranslationConfig config)
    {
        Directory.CreateDirectory(destDir);
        var written = new List<string>();
        if (config.TranslatedStrings is null || config.TranslatedStrings.Count == 0)
        {
            Log.Warn("Translation-Config ohne Uebersetzungen — nichts zu generieren");
            return written;
        }

        foreach (var lang in config.TargetLanguages)
        {
            if (!config.TranslatedStrings.TryGetValue(lang, out var strings)
                || strings.Count == 0)
            {
                Log.Info("Sprache {lang}: keine Uebersetzungen, ueberspringe", lang);
                continue;
            }

            string relDir = Path.Combine("tl", lang.ToRenpyCode());
            string absDir = Path.Combine(destDir, relDir);
            Directory.CreateDirectory(absDir);
            string filename = "krostemod_translations.rpy";
            string absPath = Path.Combine(absDir, filename);
            string relPath = Path.Combine(relDir, filename);

            var sb = new StringBuilder();
            WriteHeader(sb, lang, strings.Count);
            WriteStringsBlock(sb, lang, strings);
            File.WriteAllText(absPath, sb.ToString(), new UTF8Encoding(false));
            written.Add(relPath);
            Log.Info("Translation-Datei geschrieben: {path} ({n} Strings)", relPath, strings.Count);
        }
        return written;
    }

    private static void WriteHeader(StringBuilder sb, TargetLanguage lang, int count)
    {
        sb.AppendLine("# =====================================================================");
        sb.AppendLine($"# KrosteMod — Translation ({lang.ToPromptName()} / {lang.ToNativeName()})");
        sb.AppendLine($"# Automatisch erzeugt von RenPack. {count} Strings uebersetzt via KI.");
        sb.AppendLine($"# Im Spiel: Preferences → Language → {lang.ToNativeName()}");
        sb.AppendLine("# =====================================================================");
        sb.AppendLine();
    }

    private static void WriteStringsBlock(StringBuilder sb, TargetLanguage lang,
        IReadOnlyDictionary<string, string> strings)
    {
        sb.AppendLine($"translate {lang.ToRenpyCode()} strings:");
        sb.AppendLine();
        foreach (var (original, translated) in strings.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(translated) || translated == original) continue;
            sb.Append("    old \"").Append(EscapeForRenpy(original)).Append("\"").AppendLine();
            sb.Append("    new \"").Append(EscapeForRenpy(translated)).Append("\"").AppendLine();
            sb.AppendLine();
        }
    }

    /// <summary>Escaped einen String fuer die Ren'Py-.rpy-Syntax. Doppelte
    /// Anfuehrungszeichen und Backslashes muessen escaped werden; Escape-
    /// Sequenzen (\n, \t) sind bereits im Input-String literal enthalten
    /// (wir bekommen sie aus dem AST-Text so wie sie in der Original-.rpy
    /// standen).</summary>
    internal static string EscapeForRenpy(string s)
    {
        // Nur echte physische Zeilenumbrueche / Anfuehrungszeichen im String
        // muessen behandelt werden — Ren'Py-Escapes (\n als 2-Zeichen \, n)
        // sind schon Teil des Textes.
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\r': break;             // Ren'Py mag kein CR im String
                case '\n': sb.Append("\\n"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
