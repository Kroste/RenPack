namespace RenPack.Services.Modding;

/// <summary>Welchen Mod-Typ soll die <see cref="OneClickModBuilder"/>-
/// Pipeline erzeugen? Aktuell ist nur <c>Walkthrough</c> implementiert;
/// <c>Cheat</c> und <c>Rename</c> sind Etappen 3 und 4.</summary>
public enum ModTypeId { Walkthrough, Cheat, Rename }

/// <summary>Ergebnis einer <see cref="RenpyModAnalyzer.Analyze(string)"/>-
/// Auswertung eines dekompilierten Ren'Py-Spiels.</summary>
public sealed record ModAnalysis(
    IReadOnlyList<RpyChoice> Choices,
    IReadOnlyList<RpyStoreVariable> StoreVariables,
    IReadOnlyList<RpyCharacter> Characters,
    IReadOnlyList<string> AnalyzedFiles);

/// <summary>Ein Choice innerhalb eines <c>menu:</c>-Blocks. Die
/// <see cref="Text"/>-Property enthaelt den Choice-Text ohne umschlie-
/// ssende Anfuehrungszeichen; die <see cref="Deltas"/>-Liste zeigt was
/// der Choice mit Store-Variablen macht (aus <c>$ var += N</c> und
/// aehnlichen Statements im Choice-Body).</summary>
public sealed record RpyChoice(
    string SourceFile,   // relativer Pfad ab dem Analyse-Root
    int SourceLine,      // 1-basiert, Zeile mit "text":
    string Label,        // Name des enthaltenden `label X:`, "" wenn top-level
    int MenuIndex,       // 0-basierter Index falls mehrere Menus im Label
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
