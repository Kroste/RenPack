namespace RenPack.Services;

/// <summary>Kurz-Metadaten aus dem <c>json</c>-Eintrag eines Ren'Py-Saves.</summary>
/// <remarks>Alle Felder sind optional — Ren'Py schreibt je nach Spiel/Version mal
/// mehr, mal weniger. Was nicht drinsteht, bleibt null.</remarks>
public sealed record SaveMetadata(
    string? SaveName,
    DateTimeOffset? SaveTime,
    string? RenpyVersion,
    string? GameName,
    IReadOnlyDictionary<string, object?> Raw);

/// <summary>Eine einzelne Store-Variable aus einem Save.</summary>
/// <param name="Name">Variablenname im Ren'Py-Store (z.B. <c>money</c>, <c>_menu</c>).</param>
/// <param name="TypeName">Kurzer Typ-Name für die Anzeige (int, str, list, RevertableDict, …).</param>
/// <param name="Value">Wert-Repräsentation (String für Anzeige).</param>
/// <param name="IsInternal">true, wenn der Name mit Unterstrich beginnt (Ren'Py-intern).</param>
public sealed record SaveVariable(string Name, string TypeName, string Value, bool IsInternal);

/// <summary>Ergebnis des Save-Ladens (read-only Inspector).</summary>
/// <param name="SavePath">Absoluter Pfad zur .save-Datei.</param>
/// <param name="Metadata">Aus <c>json</c> — nie null, aber Felder können leer sein.</param>
/// <param name="ScreenshotBytes">PNG-Bytes des <c>screenshot.png</c>-Eintrags (null, wenn keiner drin).</param>
/// <param name="Variables">Extrahierte Store-Variablen (leer, wenn Log nicht dekodiert werden konnte).</param>
/// <param name="LogError">Fehlermeldung, falls der Log-Eintrag nicht entpickled werden konnte.</param>
public sealed record SaveInfo(
    string SavePath,
    SaveMetadata Metadata,
    byte[]? ScreenshotBytes,
    IReadOnlyList<SaveVariable> Variables,
    string? LogError);
