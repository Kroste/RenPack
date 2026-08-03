using System.Text.RegularExpressions;
using NLog;

namespace RenPack.Services.Modding;

/// <summary>Erkennt spielspezifische Muster aus dekompilierten .rpy-
/// Dateien und liefert ein <see cref="GameProfile"/>. Wird vor dem
/// eigentlichen Analyzer-Lauf aufgerufen, damit die Generatoren mit
/// Vorwissen ueber das Spiel arbeiten koennen.
///
/// **Design:** Single-Pass pro File, alle Regexes werden gemeinsam
/// gegen jede Zeile probiert. Kein tieferer Parser — reine
/// Heuristiken, die bei Ren'Py-typischen Coding-Styles gut greifen.
/// Falsch-Positive/Negative sind akzeptabel weil die Consumer im
/// Zweifel Fallback-Verhalten haben.</summary>
public sealed class GameProfileDetector
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary><c>config.menu_screen = "my_screen"</c> — explizite
    /// Umbelegung des Ren'Py-Standard-Menu-Screens.</summary>
    private static readonly Regex MenuScreenOverridePattern = new(
        @"config\.menu_screen\s*=\s*""([A-Za-z_][A-Za-z0-9_]*)""",
        RegexOptions.Compiled);

    /// <summary><c>screen X(items):</c> — Ren'Py-Konvention fuer Menu-
    /// Screens (der Parameter heisst per Konvention <c>items</c>).</summary>
    private static readonly Regex MenuScreenDefPattern = new(
        @"^\s*screen\s+([A-Za-z_][A-Za-z0-9_]*)\s*\([^)]*\bitems\b",
        RegexOptions.Compiled);

    /// <summary><c>define config.name = _("Title")</c> oder ohne
    /// Uebersetzungs-Wrapper.</summary>
    private static readonly Regex TitlePattern = new(
        @"define\s+config\.name\s*=\s*_?\s*\(?\s*""([^""]+)""",
        RegexOptions.Compiled);

    /// <summary><c>$ obj.update("attr", value)</c> — Container-Stat-Update.
    /// Zaehler fuer HasCharacterContainers-Heuristik.</summary>
    private static readonly Regex UpdateCallPattern = new(
        @"\$\s*[A-Za-z_][A-Za-z0-9_.]*\s*\.\s*update\s*\(",
        RegexOptions.Compiled);

    /// <summary><c>$ var op value</c> — flacher Store-Var-Assign.
    /// Zaehler fuer HasCharacterContainers-Heuristik (Vergleichsgroesse).</summary>
    private static readonly Regex FlatAssignPattern = new(
        @"^\s*\$\s+[A-Za-z_][A-Za-z0-9_]*\s*(?:=|\+=|-=|\*=|/=)",
        RegexOptions.Compiled);

    /// <summary>Choice-Header in <c>menu:</c>: <c>"text"</c> gefolgt von
    /// optionaler Condition/Args, endet mit <c>:</c>.</summary>
    private static readonly Regex ChoiceHeaderPattern = new(
        @"^\s*""(?:[^""\\]|\\.)*""\s*(?:\([^)]*\))?\s*(?:if\s+.+?)?\s*:\s*$",
        RegexOptions.Compiled);

    /// <summary><c>jump X</c> oder <c>call X</c> als erste Body-Zeile
    /// eines Choice — Signal fuer JumpBased-Style.</summary>
    private static readonly Regex ChoiceBodyJumpPattern = new(
        @"^\s*(?:jump|call)\s+[A-Za-z_]",
        RegexOptions.Compiled);

    /// <summary><c>$ ...</c> als erste Body-Zeile eines Choice — Signal
    /// fuer Inline-Style.</summary>
    private static readonly Regex ChoiceBodyDollarPattern = new(
        @"^\s*\$\s+[A-Za-z_]",
        RegexOptions.Compiled);

    public GameProfile Detect(string decompiledDir)
    {
        if (!Directory.Exists(decompiledDir))
            throw new DirectoryNotFoundException(
                $"Detector-Root nicht gefunden: {decompiledDir}");

        var menuScreens = new HashSet<string>(StringComparer.Ordinal) { "choice" };
        string? title = null;
        int updateCalls = 0;
        int flatAssigns = 0;
        int jumpChoices = 0;
        int inlineChoices = 0;
        int scannedFiles = 0;

        var root = Path.GetFullPath(decompiledDir);
        foreach (var file in Directory.EnumerateFiles(root, "*.rpy", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            // tl/ = Uebersetzungen; die screens/vars sind Duplikate der
            // Haupt-Files, wuerden nur die Zaehler verwaessern.
            if (rel.Split('/').Any(s => s.Equals("tl", StringComparison.OrdinalIgnoreCase)))
                continue;

            scannedFiles++;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                var mOverride = MenuScreenOverridePattern.Match(line);
                if (mOverride.Success) menuScreens.Add(mOverride.Groups[1].Value);

                var mScreen = MenuScreenDefPattern.Match(line);
                if (mScreen.Success) menuScreens.Add(mScreen.Groups[1].Value);

                if (title is null)
                {
                    var mTitle = TitlePattern.Match(line);
                    if (mTitle.Success) title = mTitle.Groups[1].Value;
                }

                if (UpdateCallPattern.IsMatch(line)) updateCalls++;
                else if (FlatAssignPattern.IsMatch(line)) flatAssigns++;

                // Choice-Style-Klassifikation: wenn Zeile ein Choice-Header
                // ist, schauen wir die naechste nicht-leere Body-Zeile an.
                if (ChoiceHeaderPattern.IsMatch(line))
                {
                    var first = FirstNonEmptyBodyLine(lines, i + 1);
                    if (first is not null)
                    {
                        if (ChoiceBodyJumpPattern.IsMatch(first)) jumpChoices++;
                        else if (ChoiceBodyDollarPattern.IsMatch(first)) inlineChoices++;
                        // Sonstiges (Say-Statements, Screen-Calls) zaehlen wir
                        // nicht — sind fuer die Style-Frage neutral.
                    }
                }
            }
        }

        // Translation-Languages: tl/<lang>/ Subdirs auflisten (sowohl
        // auf Root-Ebene als auch unter game/tl/ ueblich).
        var translations = FindTranslationLanguages(root);

        bool hasContainers = updateCalls >= 5 &&
            (double)updateCalls / (updateCalls + flatAssigns + 1) > 0.15;

        ChoiceStyle style = ChoiceStyle.Mixed;
        int totalChoices = jumpChoices + inlineChoices;
        if (totalChoices >= 10)
        {
            double jumpRatio = (double)jumpChoices / totalChoices;
            if (jumpRatio > 0.7) style = ChoiceStyle.JumpBased;
            else if (jumpRatio < 0.3) style = ChoiceStyle.Inline;
        }

        var profile = new GameProfile(
            MenuScreenCandidates: menuScreens.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            HasCharacterContainers: hasContainers,
            DominantChoiceStyle: style,
            TranslationLanguages: translations,
            DetectedTitle: title);

        Log.Info("GameProfile detektiert: title={title}, files={files}, "
            + "menuScreens=[{screens}], containers={containers} "
            + "(update:{upd}/flat:{flat}), style={style} "
            + "(jump:{jump}/inline:{inline}), translations=[{tl}]",
            title ?? "?", scannedFiles,
            string.Join(",", profile.MenuScreenCandidates),
            hasContainers, updateCalls, flatAssigns,
            style, jumpChoices, inlineChoices,
            string.Join(",", translations));
        return profile;
    }

    private static string? FirstNonEmptyBodyLine(string[] lines, int start)
    {
        for (int j = start; j < lines.Length; j++)
        {
            var t = lines[j].TrimStart();
            if (t.Length == 0 || t[0] == '#') continue;
            return lines[j];
        }
        return null;
    }

    private static IReadOnlyList<string> FindTranslationLanguages(string root)
    {
        var langs = new HashSet<string>(StringComparer.Ordinal);
        // Beliebig tief nach einem Ordner namens „tl" suchen und dessen
        // direkte Sub-Ordner als Sprachen behandeln.
        foreach (var tlDir in Directory.EnumerateDirectories(root, "tl", SearchOption.AllDirectories))
        {
            foreach (var lang in Directory.EnumerateDirectories(tlDir))
                langs.Add(Path.GetFileName(lang));
        }
        return langs.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }
}
