namespace RenPack.Services;

/// <summary>
/// Liest und schreibt Ren'Py-Save-Dateien (.save). Ab v0.3 mit editierbarem
/// Store: einfache Werte (int/float/str/bool/None) lassen sich byte-preserving
/// patchen — alles außerhalb der geänderten Wert-Bereiche bleibt bit-identisch,
/// insbesondere der RollbackLog und alle unbekannten Ren'Py-Klassen.
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

    /// <summary>
    /// Schreibt ein editiertes Save nach <paramref name="destinationPath"/>. Nimmt
    /// das Original-Save aus <paramref name="sourcePath"/> als Basis und ersetzt
    /// die Wert-Bytes der übergebenen <paramref name="edits"/> im log-Pickle.
    /// Alle anderen ZIP-Einträge werden 1:1 kopiert; optional wird der
    /// <c>signatures</c>-Eintrag entfernt (Standard-Verhalten, weil er nach dem
    /// Splice ungültig wäre) und <c>_save_name</c> im json aktualisiert.
    /// </summary>
    void Write(string sourcePath, string destinationPath,
        IReadOnlyList<SaveEdit> edits, string? newSaveName = null,
        bool dropSignatures = true);
}

/// <summary>Eine einzelne Wert-Änderung für <see cref="IRenpySaveService.Write"/>.
/// <paramref name="Name"/> ist der Store-Variablenname ohne <c>store.</c>-Prefix
/// (z. B. <c>money</c>), <paramref name="NewValue"/> ein einfacher .NET-Wert
/// (int/long/double/float/string/bool/null).</summary>
public sealed record SaveEdit(string Name, object? NewValue);
