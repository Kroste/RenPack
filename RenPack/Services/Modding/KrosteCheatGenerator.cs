using System.Globalization;
using System.Reflection;
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

    /// <summary>Dateiname des Cheat-Overlay-Icons im Spielordner.</summary>
    public const string CheatIconFileName = "krostemod_cheat.png";

    /// <summary>Manifest-Resource-Name des eingebetteten PNG.</summary>
    private const string CheatIconResource = "RenPack.Assets.krostemod_cheat.png";

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
        WriteOverlayIconScreen(sb);
        WriteKeymap(sb);

        File.WriteAllText(target, sb.ToString());
        ExtractCheatIcon(destDir);
        return target;
    }

    private static void ExtractCheatIcon(string destDir)
    {
        var iconPath = Path.Combine(destDir, CheatIconFileName);
        var assembly = typeof(KrosteCheatGenerator).Assembly;
        using var stream = assembly.GetManifestResourceStream(CheatIconResource)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{CheatIconResource}' nicht gefunden. " +
                "Ist Assets/krostemod_cheat.png in RenPack.csproj als <EmbeddedResource> deklariert?");
        using var fs = File.Create(iconPath);
        stream.CopyTo(fs);
    }

    /// <summary>Filtert die interessanten Cheat-Kandidaten aus der Analyse.
    /// Kriterium: int/float/bool-Typ + tatsaechlich in Choice-Delta oder
    /// Consumer verwendet. Sortiert nach Score (Deltas x2 + Consumers).</summary>
    public static IReadOnlyList<CheatCandidate> SelectCheatCandidates(ModAnalysis analysis)
    {
        var storeByName = analysis.StoreVariables
            .GroupBy(v => v.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // Delta-Aggregation: pro Variable-Name die Anzahl + der zuletzt gesehene
        // Value (fuer Default-Inferenz bei Delta-only-Vars). Wir mergen Choice-
        // Deltas UND Global-Deltas (Modifikationen ausserhalb von Menu-Choice-
        // Bodies, typisch in per-jump-erreichten label-Bodies).
        var deltaCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var deltaTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        void CollectDelta(VarDelta d)
        {
            deltaCounts[d.Variable] = deltaCounts.GetValueOrDefault(d.Variable) + 1;
            if (!deltaTypes.ContainsKey(d.Variable))
                deltaTypes[d.Variable] = InferDeltaType(d);
        }
        foreach (var choice in analysis.Choices)
            foreach (var d in choice.Deltas)
                CollectDelta(d);
        if (analysis.GlobalDeltas is { } gd)
            foreach (var d in gd) CollectDelta(d);

        var scored = new List<(string name, string kind, string defaultValue, int score)>();
        var handled = new HashSet<string>(StringComparer.Ordinal);

        // 1. Explizite Store-Variables (default / $ X = Y)
        foreach (var v in storeByName.Values)
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
            int score = deltas * 2 + consumers;
            if (score == 0) continue;

            scored.Add((v.Name, kind, v.DefaultValue, score));
            handled.Add(v.Name);
        }

        // 2. Delta-Only-Variables (typisch Character-Container-Attribute wie
        //    fcs.morality, samantha.love — werden nur ueber .update() gesetzt,
        //    kein explizites default). Ohne diese Erfassung fehlten in
        //    Boundaries of Morality die 20+ wichtigen Story-Stats.
        foreach (var (name, count) in deltaCounts)
        {
            if (handled.Contains(name)) continue;
            var kind = deltaTypes.TryGetValue(name, out var t) ? t : "int";
            if (kind != "int" && kind != "float") continue; // nur numeric — bool via .update ist selten
            int consumers = analysis.VariableConsumers.TryGetValue(name, out var cs)
                ? cs.Count : 0;
            int score = count * 2 + consumers;
            if (score == 0) continue;
            // Default fuer Delta-only-Vars: 0 fuer int, 0.0 fuer float.
            string defaultValue = kind == "float" ? "0.0" : "0";
            scored.Add((name, kind, defaultValue, score));
        }

        // Sortier-Reihenfolge: zuerst nach Type-Prio (int/float vor bool),
        // dann nach Score. Der User meinte: die meisten benoetigten Cheats
        // sind Zahlen — bool-Flags haben typischerweise nur Toggle-Wert
        // und lassen sich zur Not per Save-Editor umbiegen.
        return scored
            .OrderBy(x => TypePriority(x.kind))
            .ThenByDescending(x => x.score)
            .ThenBy(x => x.name, StringComparer.Ordinal)
            .Take(MaxCheatVars)
            .Select(x => new CheatCandidate(x.name, x.kind, x.defaultValue))
            .ToList();
    }

    /// <summary>Inferiert Type aus einem Delta-Value. `+= 1` → int,
    /// `= 3.5` → float, `= True` → bool, sonst int (Default).</summary>
    private static string InferDeltaType(VarDelta d)
    {
        var v = d.Value.Trim();
        if (v is "True" or "False") return "bool";
        if (v.Contains('.') && double.TryParse(v, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _)) return "float";
        if (long.TryParse(v.TrimStart('+', '-'), out _)) return "int";
        return "int";
    }

    private static int TypePriority(string kind) => kind switch
    {
        "int" => 0, "float" => 1, "bool" => 2, _ => 3,
    };

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
        // dotted-name-Support fuer Character-Container-Attribute wie
        // `fcs.morality`, `samantha.love`. Statt setattr(store, 'fcs.morality', v)
        // navigieren wir per Punkt-Split: store → fcs → attr=morality → value.
        sb.AppendLine("    def krostemod_cheat_resolve(name):");
        sb.AppendLine("        parts = name.split('.')");
        sb.AppendLine("        obj = store");
        sb.AppendLine("        for p in parts[:-1]:");
        sb.AppendLine("            obj = getattr(obj, p, None)");
        sb.AppendLine("            if obj is None: return None, None");
        sb.AppendLine("        return obj, parts[-1]");
        sb.AppendLine();
        sb.AppendLine("    def krostemod_cheat_get(name):");
        sb.AppendLine("        obj, attr = krostemod_cheat_resolve(name)");
        sb.AppendLine("        if obj is None: return None");
        sb.AppendLine("        try: return getattr(obj, attr)");
        sb.AppendLine("        except Exception: return None");
        sb.AppendLine();
        // WICHTIG: IMMER setattr, NIE .update() als Setter. Viele
        // Ren'Py-Container-Klassen (Boundaries of Morality's fcs, div.
        // andere Games) implementieren `update(attr, val)` additiv —
        // `fcs.update('morality', 5)` bedeutet dort `morality += 5`,
        // nicht `= 5`. Wenn wir aus adjust() den bereits berechneten
        // absoluten Ziel-Wert reingeben und dann update() aufrufen,
        // wird der Wert erneut addiert → Minus-Buttons erhoehen sogar.
        // setattr ist der zuverlaessige, semantisch-eindeutige Weg.
        sb.AppendLine("    def krostemod_cheat_set(name, value):");
        sb.AppendLine("        obj, attr = krostemod_cheat_resolve(name)");
        sb.AppendLine("        if obj is None: return");
        sb.AppendLine("        try:");
        sb.AppendLine("            setattr(obj, attr, value)");
        sb.AppendLine("        except Exception as ex:");
        sb.AppendLine("            renpy.notify('krostemod set failed: ' + str(ex))");
        sb.AppendLine();
        sb.AppendLine("    def krostemod_cheat_display(name):");
        sb.AppendLine("        try:");
        sb.AppendLine("            v = krostemod_cheat_get(name)");
        sb.AppendLine("            if v is None: return '<unset>'");
        sb.AppendLine("            s = repr(v)");
        sb.AppendLine("        except Exception:");
        sb.AppendLine("            return '<unset>'");
        sb.AppendLine("        return krostemod_cheat_escape(s)");
        sb.AppendLine();
        sb.AppendLine("    def krostemod_cheat_adjust(name, delta):");
        sb.AppendLine("        try:");
        sb.AppendLine("            v = krostemod_cheat_get(name)");
        sb.AppendLine("            if v is None: v = 0");
        sb.AppendLine("            krostemod_cheat_set(name, v + delta)");
        sb.AppendLine("            renpy.restart_interaction()");
        sb.AppendLine("        except Exception as ex:");
        sb.AppendLine("            renpy.notify('krostemod adjust failed: ' + str(ex))");
        sb.AppendLine();
        sb.AppendLine("    def krostemod_cheat_toggle(name):");
        sb.AppendLine("        try:");
        sb.AppendLine("            v = krostemod_cheat_get(name)");
        sb.AppendLine("            if v is None: v = False");
        sb.AppendLine("            krostemod_cheat_set(name, not bool(v))");
        sb.AppendLine("            renpy.restart_interaction()");
        sb.AppendLine("        except Exception as ex:");
        sb.AppendLine("            renpy.notify('krostemod toggle failed: ' + str(ex))");
        sb.AppendLine();
        sb.AppendLine("    def krostemod_cheat_reset(name):");
        sb.AppendLine("        for entry in krostemod_cheat_vars:");
        sb.AppendLine("            if entry[0] == name:");
        sb.AppendLine("                try: krostemod_cheat_set(name, entry[2])");
        sb.AppendLine("                except Exception: pass");
        sb.AppendLine("                renpy.restart_interaction()");
        sb.AppendLine("                return");
        sb.AppendLine();
        sb.AppendLine("    def krostemod_cheat_reset_all():");
        sb.AppendLine("        for entry in krostemod_cheat_vars:");
        sb.AppendLine("            try: krostemod_cheat_set(entry[0], entry[2])");
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
        sb.AppendLine();
        sb.AppendLine("    # Overlay-Screen fuer den Cheat-Icon immer aktiv registrieren.");
        sb.AppendLine("    # Der Icon-Button selbst ist immer sichtbar (Cheat kann jederzeit");
        sb.AppendLine("    # aufgerufen werden — im Gegensatz zum \"!\"-Info-Icon, das nur bei");
        sb.AppendLine("    # Choice-Menus erscheint).");
        sb.AppendLine("    if 'krostemod_cheat_overlay' not in config.overlay_screens:");
        sb.AppendLine("        config.overlay_screens.append('krostemod_cheat_overlay')");
    }

    /// <summary>Emittiert den Overlay-Screen mit dem Anonymous-Icon oben
    /// rechts. Position: <c>yalign 0.09</c> — direkt UNTER dem Info-\"!\"
    /// (das bei <c>yalign 0.02</c> sitzt). So kollidieren beide Icons
    /// nicht wenn Walkthrough+Cheat parallel installiert sind.
    ///
    /// **Warum immer sichtbar (nicht conditional)?** Cheat kann der User
    /// jederzeit brauchen — nicht nur bei Menus wie der Info-Screen. Er ist
    /// die dauerhaft-verfuegbare Toolbox.</summary>
    private static void WriteOverlayIconScreen(StringBuilder sb)
    {
        sb.AppendLine("screen krostemod_cheat_overlay():");
        sb.AppendLine("    zorder 149");
        sb.AppendLine("    imagebutton:");
        sb.AppendLine("        xalign 0.985");
        sb.AppendLine("        yalign 0.09");
        sb.AppendLine($"        idle Transform(\"{CheatIconFileName}\", alpha=0.75)");
        sb.AppendLine($"        hover Transform(\"{CheatIconFileName}\", alpha=1.0, zoom=1.08)");
        sb.AppendLine("        action ToggleScreen(\"krostemod_cheat\")");
        sb.AppendLine("        tooltip \"KrosteMod Cheat Menu (F11) — Story-Variablen bearbeiten\"");
        sb.AppendLine();
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
