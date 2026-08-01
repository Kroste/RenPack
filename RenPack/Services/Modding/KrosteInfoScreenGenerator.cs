using System.Globalization;
using System.Text;
using NLog;

namespace RenPack.Services.Modding;

/// <summary>
/// Erzeugt eine <c>krostemod_info.rpy</c>-Datei mit einem Ren'Py-Screen,
/// der ingame per <c>F10</c> aufgerufen wird und den Spieler zeigt:
///
/// <list type="bullet">
///   <item>Alle Story-Store-Variablen (aus <c>default X = Y</c>) und
///     ihren AKTUELLEN Wert live (via <c>[getattr(store, name, '?')!r]</c>).</item>
///   <item>Fuer jede Variable die Consumer-Liste — <c>label:zeile</c>-
///     Referenzen wo die Variable geprueft wird. Statisch beim Build
///     bestimmt via <see cref="RenpyModAnalyzer"/>.</item>
/// </list>
///
/// **Warum ein eigener Screen statt Choice-Tooltip?** Choice-Tooltips
/// brauchen einen Hook im spielspezifischen <c>screens.rpy</c>-Choice-
/// Screen — nicht jedes Spiel hat den. Ein eigenstaendiger Screen +
/// globales Keymap-Binding (<c>config.underlay</c>) laeuft in jedem
/// Ren'Py-Spiel ohne Anpassung des Spiel-Codes.
///
/// **Warum eine <c>.rpy</c> statt <c>.rpyc</c>?** Wir haben keinen
/// Ren'Py-Compiler in RenPack. Aber Ren'Py's Loader kompiliert
/// <c>.rpy</c>-Dateien beim Start automatisch zu <c>.rpyc</c> — der
/// Nutzer merkt nichts.
/// </summary>
public sealed class KrosteInfoScreenGenerator
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Ren'Py-Hex-Farbe fuer Ueberschriften/Highlights — passt
    /// zum Kroste-Gold aus dem Walkthrough-Hint.</summary>
    private const string GoldHex = "#e0b14c";

    /// <summary>Maximale Anzahl Consumer-Referenzen pro Variable im Screen
    /// (sonst wird die Liste bei populaeren Vars wie <c>filthy</c> zu lang).
    /// Der Rest wird als „(+N more)" angezeigt.</summary>
    private const int MaxConsumersPerVar = 8;

    /// <summary>Schreibt die <c>krostemod_info.rpy</c> nach
    /// <paramref name="destDir"/>. Gibt den erzeugten absoluten Pfad zurueck
    /// (fuer's Deploy-Manifest im <see cref="OneClickModBuilder"/>).</summary>
    public string Generate(string destDir, ModAnalysis analysis)
    {
        Directory.CreateDirectory(destDir);
        var target = Path.Combine(destDir, "krostemod_info.rpy");

        var sb = new StringBuilder();
        WriteHeader(sb);
        WriteImpactData(sb, analysis);
        WriteScreens(sb, analysis);
        WriteKeymap(sb);

        File.WriteAllText(target, sb.ToString());
        Log.Info("KrosteMod-Info-Screen erzeugt: {path} ({vars} Vars, {consumers} Consumer-Refs)",
            target, analysis.StoreVariables.Count,
            analysis.VariableConsumers.Sum(kv => kv.Value.Count));
        return target;
    }

    private static void WriteHeader(StringBuilder sb)
    {
        sb.AppendLine("# =====================================================================");
        sb.AppendLine("# KrosteMod — Variable Impact Screen");
        sb.AppendLine("# Automatisch erzeugt von RenPack. Druecke F10 im Spiel,");
        sb.AppendLine("# um die Live-Werte aller Story-Variablen + Consumer-Liste zu sehen.");
        sb.AppendLine("# =====================================================================");
        sb.AppendLine();
    }

    /// <summary>Emittiert einen Python-Init-Block mit dem Impact-Dict:
    /// <c>krostemod_impact = { "varname": [("file", line, "label", "snippet"), …] }</c>.
    /// Statisch beim Build erzeugt (analysis war beim Mod-Bau bekannt) — der
    /// Ren'Py-Screen liest das Dict zur Laufzeit ohne selbst analysieren zu muessen.</summary>
    private static void WriteImpactData(StringBuilder sb, ModAnalysis analysis)
    {
        sb.AppendLine("init 990 python:");
        sb.AppendLine("    krostemod_impact = {");

        // Nur Variablen die im Store deklariert sind ODER Consumers haben —
        // reine Local-Vars aus Python-Code sollen nicht in den Screen.
        var storeNames = new HashSet<string>(
            analysis.StoreVariables.Select(v => v.Name), StringComparer.Ordinal);
        var allNames = new SortedSet<string>(storeNames, StringComparer.Ordinal);
        foreach (var key in analysis.VariableConsumers.Keys)
            allNames.Add(key);

        foreach (var name in allNames)
        {
            var consumerList = analysis.VariableConsumers.TryGetValue(name, out var cs)
                ? cs : Array.Empty<VarConsumer>();
            sb.Append("        ");
            sb.Append(PyStr(name));
            sb.Append(": [");
            if (consumerList.Count == 0)
            {
                sb.AppendLine("],");
                continue;
            }
            sb.AppendLine();
            int emitted = 0;
            foreach (var c in consumerList.Take(MaxConsumersPerVar))
            {
                var kindTag = c.Kind == VarConsumerKind.MenuChoiceGate ? "menu" : "if";
                sb.Append("            (");
                sb.Append(PyStr(c.SourceFile));
                sb.Append(", ");
                sb.Append(c.SourceLine.ToString(CultureInfo.InvariantCulture));
                sb.Append(", ");
                sb.Append(PyStr(c.Label));
                sb.Append(", ");
                sb.Append(PyStr(kindTag));
                sb.Append(", ");
                sb.Append(PyStr(TrimSnippet(c.Snippet)));
                sb.AppendLine("),");
                emitted++;
            }
            int overflow = consumerList.Count - emitted;
            if (overflow > 0)
            {
                sb.Append("            # +");
                sb.Append(overflow.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine(" more consumer(s) (limit reached)");
            }
            sb.AppendLine("        ],");
        }
        sb.AppendLine("    }");
        sb.AppendLine();

        // Zusaetzliche Helper: aktuellen Wert lesen + Ren'Py-Text-Escape.
        // Warum Escape? repr(leeres_dict) == '{}' — und '{}' ist fuer
        // Ren'Py's Text-Tokenizer ein leeres Tag → "Empty text tag"-
        // Exception. Ren'Py-native Escape: '{' → '{{', '[' → '[['.
        // Betrifft auch Snippets (Conditions koennen list/dict-Literale
        // enthalten).
        sb.AppendLine("    def krostemod_escape(s):");
        sb.AppendLine("        if s is None: return ''");
        sb.AppendLine("        return s.replace('{', '{{').replace('[', '[[')");
        sb.AppendLine();
        sb.AppendLine("    def krostemod_get_value(name):");
        sb.AppendLine("        try:");
        sb.AppendLine("            v = getattr(store, name)");
        sb.AppendLine("            s = repr(v) if v is not None else 'None'");
        sb.AppendLine("        except Exception:");
        sb.AppendLine("            return '<unset>'");
        sb.AppendLine("        return krostemod_escape(s)");
        sb.AppendLine();
        // Setter fuer das Filter-Input: braucht eine Callable die 1 Argument
        // nimmt (den neuen Text). SetVariable(name, val) nimmt 2 fixed args
        // und der Input-changed-Handler ruft den Callback mit (new_text) auf
        // → TypeError "takes 1 positional argument but 2 were given"
        // (verifiziert an Sophia Parker 0.230, v0.9.1-Bug).
        sb.AppendLine("    def krostemod_set_filter(new_text):");
        sb.AppendLine("        store.krostemod_filter = new_text or ''");
        sb.AppendLine();
    }

    /// <summary>Emittiert den <c>screen krostemod_info</c> und den unsichtbaren
    /// <c>screen krostemod_hotkey</c>, der nur den F10-Handler traegt (der
    /// overlay-screen-Trick — der eigentliche Info-Screen ist modal und wird
    /// vom Hotkey nur ein-/ausgeblendet).</summary>
    private static void WriteScreens(StringBuilder sb, ModAnalysis analysis)
    {
        // Filter-State (Text-Suche) als Screen-Variable.
        sb.AppendLine("default krostemod_filter = \"\"");
        sb.AppendLine();

        sb.AppendLine("screen krostemod_info():");
        sb.AppendLine("    modal True");
        sb.AppendLine("    zorder 200");
        sb.AppendLine("    key \"K_F10\" action Hide(\"krostemod_info\")");
        sb.AppendLine("    key \"K_ESCAPE\" action Hide(\"krostemod_info\")");
        sb.AppendLine();
        sb.AppendLine("    frame:");
        sb.AppendLine("        xalign 0.5");
        sb.AppendLine("        yalign 0.5");
        sb.AppendLine("        xsize 1000");
        sb.AppendLine("        ysize 720");
        sb.AppendLine("        background \"#000000c0\"");
        sb.AppendLine("        padding (18, 14)");
        sb.AppendLine("        vbox:");
        sb.AppendLine("            spacing 8");
        sb.AppendLine($"            text \"KrosteMod — Variable Impact\" size 24 color \"{GoldHex}\" bold True");
        sb.AppendLine("            text \"Druecke F10 oder ESC zum Schliessen. Zeigt live-Werte aller Story-Variablen und wo sie im Spiel gecheckt werden.\" size 12 color \"#bbbbbb\"");
        sb.AppendLine("            null height 6");
        sb.AppendLine();
        sb.AppendLine("            hbox:");
        sb.AppendLine("                spacing 8");
        sb.AppendLine("                text \"Filter:\" size 14 yalign 0.5");
        sb.AppendLine("                input default krostemod_filter length 60 pixel_width 300 size 14 color \"#ffffff\" changed krostemod_set_filter");
        sb.AppendLine("                textbutton \"clear\" action SetVariable(\"krostemod_filter\", \"\") text_size 12");
        sb.AppendLine();
        sb.AppendLine("            null height 4");
        sb.AppendLine();
        sb.AppendLine("            viewport:");
        sb.AppendLine("                mousewheel True");
        sb.AppendLine("                draggable True");
        sb.AppendLine("                scrollbars \"vertical\"");
        sb.AppendLine("                xsize 960");
        sb.AppendLine("                ysize 560");
        sb.AppendLine("                vbox:");
        sb.AppendLine("                    spacing 6");
        sb.AppendLine("                    for var_name in sorted(krostemod_impact.keys()):");
        sb.AppendLine("                        if not krostemod_filter or krostemod_filter.lower() in var_name.lower():");
        sb.AppendLine("                            vbox:");
        sb.AppendLine("                                spacing 1");
        sb.AppendLine("                                hbox:");
        sb.AppendLine("                                    spacing 8");
        sb.AppendLine($"                                    text \"[var_name]\" size 14 color \"{GoldHex}\" bold True");
        sb.AppendLine("                                    text \"=\" size 14 color \"#888888\"");
        sb.AppendLine("                                    text \"[krostemod_get_value(var_name)]\" size 14 color \"#8fcfff\"");
        sb.AppendLine("                                if krostemod_impact[var_name]:");
        sb.AppendLine("                                    for entry in krostemod_impact[var_name]:");
        sb.AppendLine("                                        text \"    -> [entry[3]] in [entry[2] or entry[0]] ([entry[0]]:[entry[1]]) : [krostemod_escape(entry[4])]\" size 11 color \"#999999\"");
        sb.AppendLine("                                else:");
        sb.AppendLine("                                    text \"    (no consumers detected)\" size 11 color \"#666666\" italic True");
        sb.AppendLine("                                null height 3");
        sb.AppendLine();
        sb.AppendLine("            null height 6");
        sb.AppendLine("            textbutton \"Close (F10 / ESC)\" action Hide(\"krostemod_info\") xalign 1.0 text_size 14");
        sb.AppendLine();
    }

    /// <summary>Registriert F10 als globales Keymap-Binding (funktioniert
    /// auch waehrend Menu/Say-Interactions). Nutzt Ren'Py's
    /// <c>config.underlay</c> mit <c>renpy.Keymap</c>.</summary>
    private static void WriteKeymap(StringBuilder sb)
    {
        sb.AppendLine("init 999 python:");
        sb.AppendLine("    def _krostemod_toggle_info():");
        sb.AppendLine("        if renpy.get_screen('krostemod_info'):");
        sb.AppendLine("            renpy.hide_screen('krostemod_info')");
        sb.AppendLine("        else:");
        sb.AppendLine("            renpy.show_screen('krostemod_info')");
        sb.AppendLine("        renpy.restart_interaction()");
        sb.AppendLine();
        sb.AppendLine("    config.keymap.setdefault('krostemod_toggle', []).append('K_F10')");
        sb.AppendLine("    config.underlay.append(renpy.Keymap(krostemod_toggle=_krostemod_toggle_info))");
    }

    /// <summary>Python-safe String-Literal — verwendet <c>repr()</c>-aehnliche
    /// Escape-Regeln fuer die Ren'Py-Init-Python-Ausgabe. Wir brauchen die
    /// Werte in einem <c>init python:</c>-Block, wo Ren'Py-Text-Substitution
    /// NICHT greift (nur in Say-Strings) — also reicht Python-Escape.</summary>
    private static string PyStr(string? s)
    {
        if (s is null) return "None";
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

    /// <summary>Kuerzt einen Condition-Snippet auf max 60 Zeichen — sonst
    /// wird der Screen zu breit bei komplexen Ausdruecken (z.B.
    /// <c>if love >= 5 and respect > 0 and not day22_visited: …</c>).</summary>
    private static string TrimSnippet(string s)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= 60 ? s : s[..57] + "...";
    }
}
