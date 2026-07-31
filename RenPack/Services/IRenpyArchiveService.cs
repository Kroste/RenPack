namespace RenPack.Services;

/// <summary>Fortschrittsmeldung bei Extraktion/Erstellung.</summary>
public readonly record struct RpaProgress(int Current, int Total, string CurrentFile)
{
    public double Fraction => Total <= 0 ? 0 : (double)Current / Total;
}

/// <summary>
/// Liest, entpackt und erstellt Ren'Py-Archive (.rpa). Die gesamte Formatlogik lebt
/// hier (testbar, UI-frei) — ViewModels delegieren nur.
/// </summary>
public interface IRenpyArchiveService
{
    /// <summary>Liest den Index eines Archivs (Format, Key, Dateiliste) ohne zu entpacken.</summary>
    RpaArchiveInfo ReadIndex(string archivePath);

    /// <summary>Entpackt eine einzelne Datei aus dem Archiv nach <paramref name="destinationFile"/>.</summary>
    void ExtractEntry(string archivePath, RpaEntry entry, string destinationFile);

    /// <summary>
    /// Entpackt ausgewählte Einträge in ein Zielverzeichnis (Ordnerstruktur bleibt erhalten).
    /// Gibt die Anzahl entpackter Dateien zurück.
    /// </summary>
    int Extract(RpaArchiveInfo archive, IEnumerable<RpaEntry> entries, string destinationDirectory,
        IProgress<RpaProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Entpackt alle Einträge des Archivs in ein Zielverzeichnis.</summary>
    int ExtractAll(RpaArchiveInfo archive, string destinationDirectory,
        IProgress<RpaProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Erstellt ein neues Archiv aus allen Dateien unter <paramref name="sourceDirectory"/>
    /// (rekursiv, relative Pfade als Archivnamen). Gibt die Anzahl gepackter Dateien zurück.
    /// </summary>
    int Create(string archivePath, string sourceDirectory,
        RpaVersion version = RpaVersion.V3_0, uint key = RenpyArchiveService.DefaultKey,
        IProgress<RpaProgress>? progress = null, CancellationToken cancellationToken = default);
}
