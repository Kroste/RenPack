namespace RenPack.Services;

/// <summary>Unterstützte Ren'Py-Archiv-Formate.</summary>
public enum RpaVersion
{
    /// <summary>RPA-2.0 — Index ohne XOR-Verschleierung, kein Key.</summary>
    V2_0,
    /// <summary>RPA-3.0 — Offsets/Längen mit 32-bit-Key XOR-verschleiert (Standard).</summary>
    V3_0,
    /// <summary>RPA-3.2 — wie 3.0, Header mit zusätzlichem Feld (`RPA-3.2 &lt;offset&gt; 0 &lt;key&gt;`).</summary>
    V3_2,
}

/// <summary>
/// Ein zusammenhängender Datenabschnitt einer archivierten Datei. Die meisten Dateien
/// bestehen aus genau einem Segment; das Format erlaubt aber mehrere.
/// Die tatsächlichen Dateibytes eines Segments sind:
/// <c>Prefix + Archivbytes[Offset .. Offset + (Length - Prefix.Length)]</c>.
/// </summary>
public sealed record RpaSegment(long Offset, long Length, byte[] Prefix)
{
    /// <summary>Anzahl Bytes, die für dieses Segment aus dem Archiv gelesen werden.</summary>
    public long BytesFromArchive => Length - Prefix.Length;
}

/// <summary>Eine Datei im Archiv (ein oder mehrere Segmente).</summary>
public sealed record RpaEntry(string Path, IReadOnlyList<RpaSegment> Segments)
{
    /// <summary>Gesamtgröße der entpackten Datei in Bytes.</summary>
    public long Size => Segments.Sum(s => s.Length);
}

/// <summary>Anzeigename der Formatversion.</summary>
public static class RpaVersionExtensions
{
    public static string ToDisplay(this RpaVersion version) => version switch
    {
        RpaVersion.V2_0 => "RPA-2.0",
        RpaVersion.V3_0 => "RPA-3.0",
        RpaVersion.V3_2 => "RPA-3.2",
        _ => version.ToString(),
    };
}

/// <summary>Ergebnis des Einlesens eines Archiv-Index: Format, Key und Dateiliste.</summary>
public sealed record RpaArchiveInfo(
    string ArchivePath,
    RpaVersion Version,
    uint Key,
    IReadOnlyList<RpaEntry> Entries)
{
    public long TotalSize => Entries.Sum(e => e.Size);
}
