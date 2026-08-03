namespace RenPack.Services.Modding;

/// <summary>Welchen Mod-Typ soll die <see cref="OneClickModBuilder"/>-
/// Pipeline erzeugen? Walkthrough/Cheat/Rename sind E1-E4; Translate ist
/// E6 (Ren'Py-tl-basierte Voll-Uebersetzung per KI-Batch).</summary>
public enum ModTypeId { Walkthrough, Cheat, Rename, Translate }

/// <summary>Ergebnis einer <see cref="RenpyModAnalyzer.Analyze(string)"/>-
/// Auswertung eines dekompilierten Ren'Py-Spiels.
///
/// <see cref="VariableConsumers"/> mappt jeden Variablennamen auf die Liste
/// aller Stellen, wo die Variable geLESEN wird (in <c>if</c>/<c>elif</c>-
/// Conditions, Menu-Choice-Conditions). Fuer den KrosteMod-Info-Screen
/// (v0.9.0) wichtig — dem Spieler zeigen wo eine gerade veraenderte Variable
/// spaeter Konsequenzen hat.
///
/// <see cref="MenuLocations"/> fuer v0.9.3: pro <c>menu:</c>-Header speichern
/// wir Datei + Zeilennummer + Liste aller Variablen die die Choices im
/// Menu setzen. Der ingame-Overlay-Screen kann ueber
/// <c>renpy.get_filename_line()</c> das aktuelle Menu identifizieren und den
/// „!"-Hint nur mit den relevanten Variablen anzeigen.</summary>
public sealed record ModAnalysis(
    IReadOnlyList<RpyChoice> Choices,
    IReadOnlyList<RpyStoreVariable> StoreVariables,
    IReadOnlyList<RpyCharacter> Characters,
    IReadOnlyList<string> AnalyzedFiles,
    IReadOnlyDictionary<string, IReadOnlyList<VarConsumer>> VariableConsumers,
    IReadOnlyList<RpyMenuLocation> MenuLocations,
    IReadOnlyList<RpySayStatement> SayStatements,
    IReadOnlyList<VarDelta>? GlobalDeltas = null);

/// <summary>Ein <c>character "text"</c>-Statement (Say). Fuer den
/// KrosteMod-E4b-Rewriter: wenn User einen Character umbenennt und die
/// KI soll den Body-Text konsistent umschreiben, brauchen wir alle
/// Say-Stellen mit ihrer Position um sie in der .rpy zu patchen.
///
/// <see cref="CharacterVar"/> ist der Python-Identifier links vom Text
/// (z.B. <c>sophia</c> in <c>sophia "Hallo"</c>) oder leer bei Narrator-
/// Text. <see cref="RawTextInFile"/> ist der Text OHNE die umschliessen-
/// den Anfuehrungszeichen aber MIT Escape-Sequenzen (\", \n) — so wie
/// er in der .rpy steht. Wird gebraucht damit der Patcher die richtige
/// Zeile exact ersetzen kann.</summary>
public sealed record RpySayStatement(
    string SourceFile,
    int SourceLine,
    string CharacterVar,
    string RawTextInFile);

/// <summary>Eine <c>menu:</c>-Stelle im Skript. <see cref="MenuHeaderLine"/>
/// ist die 1-basierte Zeilennummer des <c>menu:</c> (bzw. <c>menu name:</c>)
/// in der dekompilierten .rpy — mit dieser Zeile matched
/// <c>renpy.get_filename_line()</c> zur Laufzeit. <see cref="VariablesAffected"/>
/// ist die Union aller Variablen, die die Choices in diesem Menu via
/// <c>$ var op value</c> aendern.</summary>
public sealed record RpyMenuLocation(
    string SourceFile,
    int MenuHeaderLine,
    IReadOnlyList<string> VariablesAffected);

/// <summary>Eine Stelle im Skript, an der eine Variable GELESEN wird.
/// Beispiel: <c>if love >= 5:</c> in <c>day22.rpy</c> Zeile 234, im Label
/// <c>day22_confession</c>. <see cref="Kind"/> unterscheidet die Kontexte
/// fuer die Anzeige („checked in condition" vs „menu gated").</summary>
public sealed record VarConsumer(
    string SourceFile,
    int SourceLine,
    string Label,
    VarConsumerKind Kind,
    string Snippet);

public enum VarConsumerKind
{
    /// <summary><c>if var == …</c> / <c>elif</c> / <c>while</c>.</summary>
    Condition,
    /// <summary>Choice-Condition: <c>"text" if var:</c>.</summary>
    MenuChoiceGate,
}

/// <summary>Ein Choice innerhalb eines <c>menu:</c>-Blocks. Die
/// <see cref="Text"/>-Property enthaelt den Choice-Text ohne umschlie-
/// ssende Anfuehrungszeichen; die <see cref="Deltas"/>-Liste zeigt was
/// der Choice mit Store-Variablen macht (aus <c>$ var += N</c> und
/// aehnlichen Statements im Choice-Body).
///
/// <see cref="MenuHeaderLine"/> ist die 1-basierte Zeile des umgebenden
/// <c>menu:</c>-Headers — wird fuer den Runtime-Match des Overlay-Hints
/// via <c>renpy.get_filename_line()</c> gebraucht (v0.9.3).</summary>
public sealed record RpyChoice(
    string SourceFile,   // relativer Pfad ab dem Analyse-Root
    int SourceLine,      // 1-basiert, Zeile mit "text":
    string Label,        // Name des enthaltenden `label X:`, "" wenn top-level
    int MenuIndex,       // 0-basierter Index falls mehrere Menus im Label
    int MenuHeaderLine,  // 1-basiert, Zeile des `menu:`-Headers
    int ChoiceIndex,     // 0-basierter Index innerhalb des Menus
    string Text,         // Choice-Text (ohne Quotes, ohne trailing colon)
    string? Condition,   // "if …"-Bedingung nach dem Choice-Text, falls vorhanden
    IReadOnlyList<VarDelta> Deltas);

/// <summary>Ein Store-Variablen-Zugriff im Body eines Choices oder Labels.
/// <see cref="Op"/> ist einer von: "+=" "-=" "=" "*=" "/=" oder "call"
/// (fuer Setzen auf True/False durch Zuweisung).</summary>
public sealed record VarDelta(string Variable, string Op, string Value);

/// <summary>Store-Variable aus einem <c>default X = Y</c>-Statement.
/// <see cref="TypeInferred"/> wird aus dem Default-Wert geraten
/// (int/float/str/bool/None/expr).</summary>
public sealed record RpyStoreVariable(
    string Name, string DefaultValue, string TypeInferred);

/// <summary>Character-Definition aus <c>define X = Character(...)</c>.
/// Wird fuer den KI-Rename-Patch (E4) gebraucht.</summary>
public sealed record RpyCharacter(
    string VarName, string DisplayName, string? Color);
