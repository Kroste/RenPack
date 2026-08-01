using System.Globalization;
using System.Text;
using NLog;

namespace RenPack.Services.Modding;

/// <summary>
/// Erzeugt eine <c>krostemod_cheat.rpy</c> mit einem Ren'Py-Screen der
/// ingame per <c>F11</c> aufgerufen wird und dem Spieler erlaubt, Story-
/// Variablen direkt zu manipulieren — Sliders sind zu tricky in Ren'Py's
/// Screen-Language, also machen wir <c>[-10] [-1] [+1] [+10]</c>-Buttons
/// fuer int/float und <c>[Toggle]</c> fuer bool. <c>[reset]</c> setzt auf
/// den <c>default</c>-Wert aus dem Original-.rpy zurueck.
///
/// **Welche Vars sind Kandidaten?** Der komplette Store hat oft 400+
/// Variablen — ein Cheat-Menu daraus waere unbenutzbar. Wir filtern:
/// nur int/float/bool-Vars die in mindestens einem Choice-Delta ODER in
/// einer if/menu-Condition auftauchen. Das sind die tatsaechlichen
/// „Story-Stats" — Flags/Zaehler die das Spiel liest.
/// Sortiert nach Aenderungshaeufigkeit (Top-Stats zuerst), Cap bei 40.
/// </summary>
public sealed class KrosteCheatGenerator
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const string GoldHex = "#e0b14c";

    /// <summary>Max Anzahl Cheat-Vars im Screen — mehr wird unbenutzbar
    /// (Scrollen wird zur Qual, Overview geht verloren).</summary>
    private const int MaxCheatVars = 40;

    /// <summary>Schreibt die <c>krostemod_cheat.rpy</c> nach
    /// <paramref name="destDir"/>. Gibt den erzeugten absoluten Pfad zurueck.</summary>
    public string Generate(string destDir, ModAnalysis analysis)
    {
        Directory.CreateDirectory(destDir);
        var target = Path.Combine(destDir, "krostemod_cheat.rpy");

        var cheatVars = SelectCheatCandidates(analysis);
        Log.Info("KrosteMod-Cheat: {count} Cheat-Kandidaten aus {totalVars} StoreVars",
            cheatVars.Count, analysis.StoreVariables.Count);

        var sb = new StringBuilder();
        WriteHeader(sb, cheatVars.Count);
        WriteCheatData(sb, cheatVars);
        WriteHelpers(sb);
        WriteScreen(sb);
        WriteKeymap(sb);

        File.WriteAllText(target, sb.ToString());
        return target;
    }

    /// <summary>Filtert die interessanten Cheat-Kandidaten aus der Analyse.
    /// Kriterium: int/float/bool-Typ + tatsaechlich in Choice-Delta oder
    /// Consumer verwendet. Sortiert nach Score (Deltas x2 + Consumers).</summary>
    public static IReadOnlyList<CheatCandidate> SelectCheatCandidates(ModAnalysis analysis)
    {
        var storeByName = analysis.StoreVariables
            .GroupBy(v => v.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var deltaCounts = analysis.Choices
            .SelectMany(c => c.Deltas)
            .GroupBy(d => d.Variable, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var scored = new List<(string name, string kind, string defaultValue, int score)>();
        foreach (var v in analysis.StoreVariables)
        {
            var kind = v.TypeInferred switch
            {
                "int" or "float" or "bool" => v.TypeInferred,
                _ => null,
            };
            if (kind is null) continue;

            deltaCounts.TryGetValue(v.Name, out int deltas);
            int consumers = analysis.VariableConsumers.TryGetValue(v.Name, out var cs)
                ? cs.Count : 0;
            // Deltas zaehlen doppelt — die sind der staerkere Impact-Indikator.
            int score = deltas * 2 + consumers;
            if (score == 0) continue;

            scored.Add((v.Name, kind, v.DefaultValue, score));
        }

        return scored
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.name, StringComparer.Ordinal)
            .Take(MaxCheatVars)
            .Select(x => new CheatCandidate(x.name, x.kind, x.defaultValue))
            .ToList();
    }

    private static void WriteHeader(StringBuilder sb, int count)
    {
        sb.AppendLine("# =====================================================================");
        sb.AppendLine("# KrosteMod — Cheat Menu");
        sb.AppendLine("# Automatisch erzeugt von RenPack. Druecke F11 im Spiel um das");
        sb.AppendLine($"# Cheat-Menu zu oeffnen — {count} Story-Variable(n) zum Manipulieren.");
        sb.AppendLine("# =====================================================================");
        sb.AppendLine();
    }

    /// <summary>Emittiert die Vars-Tabelle als Python-Liste von 3-Tupeln
    /// <c>(name, kind, default_value)</c>. Der Screen iteriert darueber.</summary>
    private static void WriteCheatData(StringBuilder sb, IReadOnlyList<CheatCandidate> vars)
    {
        sb.AppendLine("init 985 python:");
        sb.AppendLine("    krostemod_cheat_vars = [");
        foreach (var v in vars)
        {
            sb.Append("        (");
            sb.Append(PyStr(v.Name));
            sb.Append(", ");
            sb.Append(PyStr(v.Kind));
            sb.Append(", ");
            sb.Append(PyDefault(v));
            sb.AppendLine("),");
        }
        sb.AppendLine("    ]");
        sb.AppendLine();
    }

    /// <summary>Wandelt den Default-Value (String aus dem .rpy) in ein
    /// Python-Literal um. Ren'Py's <c>default X = Y</c> ist Python-Syntax,
    /// also koennen wir die Rohform durchreichen — mit Fallback fuer
    /// exotische Expressions (die dann als String bleiben und beim Reset
    /// ignoriert werden).</summary>
    private static string PyDefault(CheatCandidate v)
    {
        var raw = v.DefaultValue.Trim();
        // Fuer int/float/bool ist der Raw-Wert bereits ein Python-Literal
        // (z.B. "0", "0.5", "True") — direkt uebernehmen.
        if (v.Kind is "int" or "float" or "bool")
            return raw;
        return PyStr(raw);
    }

    /// <summary>Helper-Functions fuer Adjust/Toggle/Reset + Display-
    /// Escape (fuer den <c>{}</c>-Bug siehe KrosteInfoScreenGenerator).</summary>
    private static void WriteHelpers(StringBuilder sb)
    {
        sb.AppendLine("init 986 python:");
        sb.AppendLine("    def krostemod_cheat_escape(s):");
        sb.AppendLine("        if s is None: return ''");
        sb.AppendLine("        return s.replace('{', '{{').replace('[', '[[')");
        sb.AppendLine();
        sb.AppendLine("    def krostemod_cheat_display(name):");
        sb.AppendLine("        try:");
        sb.AppendLine("            v = getattr(store, name)");
        sb.AppendLine("            s = repr(v) if v is not None else 'None'");
        sb.AppendLine("        except Exception:");
        sb.AppendLine("            return '<unset>'");
        sb.AppendLine("        return krostemod_cheat_escape(s)");
        sb.AppendLine();
        sb.AppendLine("    def krostemod_cheat_adjust(name, delta):");
        sb.AppendLine("        try:");
        sb.AppendLine("            v = getattr(store, name, 0)");
        sb.AppendLine("            if v is None: v = 0");
        sb.AppendLine("            setattr(store, name, v + delta)");
        sb.AppendLine("            renpy.restart_interaction()");
        sb.AppendLine("        except Exception as ex:");
        sb.AppendLine("            renpy.notify('krostemod adjust failed: ' + str(ex))");
        sb.AppendLine();
        sb.AppendLine("    def krostemod_cheat_toggle(name):");
        sb.AppendLine("        try:");
        sb.AppendLine("            v = getattr(store, name, False)");
        sb.AppendLine("            setattr(store, name, not bool(v))");
        sb.AppendLine("            renpy.restart_interaction()");
        sb.AppendLine("        except Exception as ex:");
        sb.AppendLine("            renpy.notify('krostemod toggle failed: ' + str(ex))");
        sb.AppendLine();
        sb.AppendLine("    def krostemod_cheat_reset(name):");
        sb.AppendLine("        for entry in krostemod_cheat_vars:");
        sb.AppendLine("            if entry[0] == name:");
        sb.AppendLine("                try: setattr(store, name, entry[2])");
        sb.AppendLine("                except Exception: pass");
        sb.AppendLine("                renpy.restart_interaction()");
        sb.AppendLine("                return");
        sb.AppendLine();
        sb.AppendLine("    def krostemod_cheat_reset_all():");
        sb.AppendLine("        for entry in krostemod_cheat_vars:");
        sb.AppendLine("            try: setattr(store, entry[0], entry[2])");
        sb.AppendLine("            except Exception: pass");
        sb.AppendLine("        renpy.restart_interaction()");
        sb.AppendLine();
    }

    private static void WriteScreen(StringBuilder sb)
    {
        sb.AppendLine("default krostemod_cheat_filter = \"\"");
        sb.AppendLine();
        sb.AppendLine("init 987 python:");
        sb.AppendLine("    def krostemod_set_cheat_filter(new_text):");
        sb.AppendLine("        store.krostemod_cheat_filter = new_text or ''");
        sb.AppendLine();
        sb.AppendLine("screen krostemod_cheat():");
        sb.AppendLine("    modal True");
        sb.AppendLine("    zorder 220");
        sb.AppendLine("    key \"K_F11\" action Hide(\"krostemod_cheat\")");
        sb.AppendLine("    key \"K_ESCAPE\" action Hide(\"krostemod_cheat\")");
        sb.AppendLine();
        sb.AppendLine("    frame:");
        sb.AppendLine("        xalign 0.5");
        sb.AppendLine("        yalign 0.5");
        sb.AppendLine("        xsize 960");
        sb.AppendLine("        ysize 700");
        sb.AppendLine("        background \"#000000d0\"");
        sb.AppendLine("        padding (18, 14)");
        sb.AppendLine("        vbox:");
        sb.AppendLine("            spacing 8");
        sb.AppendLine($"            text \"KrosteMod — Cheat Menu\" size 24 color \"{GoldHex}\" bold True");
        sb.AppendLine("            text \"F11 oder ESC zum Schliessen. Bearbeite Story-Variablen direkt.\" size 12 color \"#bbbbbb\"");
        sb.AppendLine("            null height 4");
        sb.AppendLine();
        sb.AppendLine("            hbox:");
        sb.AppendLine("                spacing 8");
        sb.AppendLine("                text \"Filter:\" size 14 yalign 0.5");
        sb.AppendLine("                input default krostemod_cheat_filter length 60 pixel_width 300 size 14 color \"#ffffff\" changed krostemod_set_cheat_filter");
        sb.AppendLine("                textbutton \"clear\" action SetVariable(\"krostemod_cheat_filter\", \"\") text_size 12");
        sb.AppendLine("                textbutton \"Reset all\" action Function(krostemod_cheat_reset_all) text_size 12");
        sb.AppendLine();
        sb.AppendLine("            null height 4");
        sb.AppendLine();
        sb.AppendLine("            viewport:");
        sb.AppendLine("                mousewheel True");
        sb.AppendLine("                draggable True");
        sb.AppendLine("                scrollbars \"vertical\"");
        sb.AppendLine("                xsize 920");
        sb.AppendLine("                ysize 550");
        sb.AppendLine("                vbox:");
        sb.AppendLine("                    spacing 4");
        sb.AppendLine("                    for entry in krostemod_cheat_vars:");
        sb.AppendLine("                        $ ce_name = entry[0]");
        sb.AppendLine("                        $ ce_kind = entry[1]");
        sb.AppendLine("                        if not krostemod_cheat_filter or krostemod_cheat_filter.lower() in ce_name.lower():");
        sb.AppendLine("                            hbox:");
        sb.AppendLine("                                spacing 6");
        sb.AppendLine("                                yalign 0.5");
        sb.AppendLine($"                                text \"[ce_name]\" size 13 color \"{GoldHex}\" bold True xsize 260");
        sb.AppendLine("                                text \"=\" size 13 color \"#888888\"");
        sb.AppendLine("                                text \"[krostemod_cheat_display(ce_name)]\" size 13 color \"#8fcfff\" xsize 140");
        sb.AppendLine("                                if ce_kind == \"bool\":");
        sb.AppendLine("                                    textbutton \"Toggle\" action Function(krostemod_cheat_toggle, ce_name) text_size 12");
        sb.AppendLine("                                else:");
        sb.AppendLine("                                    textbutton \"-10\" action Function(krostemod_cheat_adjust, ce_name, -10) text_size 12");
        sb.AppendLine("                                    textbutton \"-1\"  action Function(krostemod_cheat_adjust, ce_name, -1) text_size 12");
        sb.AppendLine("                                    textbutton \"+1\"  action Function(krostemod_cheat_adjust, ce_name, 1) text_size 12");
        sb.AppendLine("                                    textbutton \"+10\" action Function(krostemod_cheat_adjust, ce_name, 10) text_size 12");
        sb.AppendLine("                                textbutton \"reset\" action Function(krostemod_cheat_reset, ce_name) text_size 12");
        sb.AppendLine();
        sb.AppendLine("            null height 6");
        sb.AppendLine("            textbutton \"Close (F11 / ESC)\" action Hide(\"krostemod_cheat\") xalign 1.0 text_size 14");
        sb.AppendLine();
    }

    private static void WriteKeymap(StringBuilder sb)
    {
        sb.AppendLine("init 999 python:");
        sb.AppendLine("    def _krostemod_toggle_cheat():");
        sb.AppendLine("        if renpy.get_screen('krostemod_cheat'):");
        sb.AppendLine("            renpy.hide_screen('krostemod_cheat')");
        sb.AppendLine("        else:");
        sb.AppendLine("            renpy.show_screen('krostemod_cheat')");
        sb.AppendLine("        renpy.restart_interaction()");
        sb.AppendLine();
        sb.AppendLine("    config.keymap.setdefault('krostemod_cheat_toggle', []).append('K_F11')");
        sb.AppendLine("    config.underlay.append(renpy.Keymap(krostemod_cheat_toggle=_krostemod_toggle_cheat))");
    }

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
}

/// <summary>Ein Cheat-Kandidat — eine Store-Variable die im Cheat-Screen
/// erscheinen soll. Kind ist eine von <c>"int" | "float" | "bool"</c>.</summary>
public sealed record CheatCandidate(string Name, string Kind, string DefaultValue);
