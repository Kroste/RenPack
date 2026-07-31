namespace RenPack.Services;

/// <summary>
/// Liest Ren'Py-Save-Dateien (.save). v0.2 ist read-only: JSON-Metadaten,
/// Screenshot und Store-Variablen des zuletzt gespeicherten Zustands.
/// Schreibsupport folgt in v0.3.
/// </summary>
public interface IRenpySaveService
{
    /// <summary>
    /// Öffnet ein Save und extrahiert Metadaten, Screenshot und Store-Variablen.
    /// Wirft nur, wenn die Datei kein gültiges ZIP ist oder gar keine Save-Struktur
    /// erkennbar ist — teilweise Fehler (Log unlesbar, Screenshot fehlt) landen im
    /// zurückgegebenen <see cref="SaveInfo"/>.
    /// </summary>
    SaveInfo Read(string savePath);
}
