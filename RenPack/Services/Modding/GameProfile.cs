namespace RenPack.Services.Modding;

/// <summary>Erkennungs-Ergebnis fuer ein dekompiliertes Ren'Py-Spiel.
/// Zentralisiert die spielspezifischen Muster (Custom-Menu-Screens,
/// Character-Container-Stats, Choice-Style, Translations), damit
/// Analyzer und Generatoren an EINER Stelle abfragen koennen wie das
/// Spiel „tickt" statt jede Baustelle eigene Heuristiken zu haben.
///
/// **Bewusst als Record ohne Interface:** die erkannten Merkmale sind
/// orthogonal (Boundaries hat gleichzeitig Custom-Screen + Container-
/// Stats + Jump-Choices). Ein polymorphes Profile-Interface waere
/// eine Klassen-Explosion (2^N Kombinationen) — Config-Record ist
/// simpler und leicht testbar.</summary>
/// <param name="MenuScreenCandidates">Alle Screens die als Menu-Screen
/// in Frage kommen. Default: <c>["choice"]</c>. Erweitert um Screens
/// die einen <c>items</c>-Parameter haben und um explizite
/// <c>config.menu_screen = "..."</c>-Overrides.</param>
/// <param name="HasCharacterContainers">Ob das Spiel Stats primaer in
/// Character-Container-Objekten haelt (<c>$ fcs.update("morality", 1)</c>)
/// statt als flache Store-Vars (<c>$ love += 1</c>). Signal fuer den
/// Cheat-Generator dotted-Namen im UI passend aufzudroeseln.</param>
/// <param name="DominantChoiceStyle">Ob die meisten Choice-Bodies nur
/// aus einem <c>jump X</c> bestehen (JumpBased) oder direkt Deltas
/// enthalten (Inline). Beeinflusst wie aggressiv Jump-Follow greift.</param>
/// <param name="TranslationLanguages">Liste der <c>tl/&lt;lang&gt;/</c>-
/// Sprachen. Signal fuer den Walkthrough-Generator in Translation-Aware
/// Mode zu wechseln.</param>
/// <param name="DetectedTitle">Aus <c>define config.name = _("...")</c>
/// extrahiert. Fuer Log-Zwecke, nicht funktional relevant.</param>
public sealed record GameProfile(
    IReadOnlyList<string> MenuScreenCandidates,
    bool HasCharacterContainers,
    ChoiceStyle DominantChoiceStyle,
    IReadOnlyList<string> TranslationLanguages,
    string? DetectedTitle);

/// <summary>Wie sind die Choices in <c>menu:</c>-Bloecken typischerweise
/// programmiert?</summary>
public enum ChoiceStyle
{
    /// <summary>Choice-Body enthaelt direkt <c>$ var op value</c>-
    /// Statements. Fuer Delta-Extraktion trivial.</summary>
    Inline,
    /// <summary>Choice-Body enthaelt fast nur <c>jump X</c>, die
    /// tatsaechlichen Deltas liegen im Ziel-Label. Fuer Delta-Extraktion
    /// braucht der Analyzer Jump-Follow.</summary>
    JumpBased,
    /// <summary>Gemischt — beide Muster kommen etwa gleich haeufig vor.</summary>
    Mixed,
}
