using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using NLog;
using Razorvine.Pickle;

namespace RenPack.Services;

/// <summary>
/// Liest und schreibt Ren'Py-Archive (.rpa). Format-Referenz: Ren'Py loader.py,
/// unrpa und rpatool.
///
/// Aufbau eines Archivs:
///   1. Header-Zeile (ASCII, endet mit \n):
///      RPA-3.0  "RPA-3.0 &lt;indexOffset:16hex&gt; &lt;key:8hex&gt;\n"
///      RPA-3.2  "RPA-3.2 &lt;indexOffset:16hex&gt; 0 &lt;key:8hex&gt;\n"
///      RPA-2.0  "RPA-2.0 &lt;indexOffset:16hex&gt;\n"  (kein Key)
///   2. Danach die rohen Dateidaten.
///   3. Ab &lt;indexOffset&gt;: zlib-komprimiertes Python-Pickle des Index.
///      Index = dict{ Dateiname : [ [offset, length, (prefix)] , ... ] }.
///      Bei 3.0/3.2 sind offset und length mit dem Key XOR-verschleiert.
/// </summary>
public sealed class RenpyArchiveService : IRenpyArchiveService
{
    /// <summary>Standard-Key beim Erstellen von RPA-3.0/3.2 (wie rpatool).</summary>
    public const uint DefaultKey = 0xDEADBEEF;

    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public RpaArchiveInfo ReadIndex(string archivePath)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Info("Lese RPA-Index: {path}", archivePath);

        using var fs = File.OpenRead(archivePath);
        var line = ReadHeaderLine(fs).TrimEnd('\r');
        Log.Trace("Header-Zeile: {line}", line);

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new NotSupportedException(
                "Datei hat keinen gültigen RPA-Header. Handelt es sich wirklich um ein Ren'Py-Archiv?");

        RpaVersion version;
        long indexOffset;
        uint key;
        switch (parts[0])
        {
            case "RPA-3.0":
                version = RpaVersion.V3_0;
                indexOffset = ParseHex(parts[1]);
                key = (uint)ParseHex(parts[2]);
                break;
            case "RPA-3.2":
                version = RpaVersion.V3_2;
                indexOffset = ParseHex(parts[1]);
                key = (uint)ParseHex(parts[3]); // parts[2] ist ein Füllfeld ("0")
                break;
            case "RPA-2.0":
                version = RpaVersion.V2_0;
                indexOffset = ParseHex(parts[1]);
                key = 0;
                break;
            default:
                throw new NotSupportedException(
                    $"Nicht unterstütztes Format '{parts[0]}'. Unterstützt: RPA-2.0, RPA-3.0, RPA-3.2. " +
                    "RPA-1.0 (mit separater .rpi-Datei) wird nicht unterstützt.");
        }

        Log.Debug("Format {ver}, IndexOffset 0x{off:x}, Key 0x{key:x8}", version, indexOffset, key);

        fs.Seek(indexOffset, SeekOrigin.Begin);
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            fs.CopyTo(ms);
            compressed = ms.ToArray();
        }

        byte[] indexBytes = ZlibDecompress(compressed);
        object rawIndex;
        using (var unpickler = new Unpickler())
            rawIndex = unpickler.loads(indexBytes);

        if (rawIndex is not IDictionary dict)
            throw new InvalidDataException("Der Archiv-Index hat ein unerwartetes Format (kein Dictionary).");

        var entries = new List<RpaEntry>(dict.Count);
        foreach (DictionaryEntry de in dict)
        {
            string name = KeyToPath(de.Key);
            var segments = new List<RpaSegment>();
            foreach (var segObj in AsList(de.Value))
            {
                var tuple = AsList(segObj);
                long offset = ToInt64(tuple[0]) ^ key;
                long length = ToInt64(tuple[1]) ^ key;
                byte[] prefix = tuple.Count > 2 ? ToBytes(tuple[2]) : [];
                segments.Add(new RpaSegment(offset, length, prefix));
            }
            entries.Add(new RpaEntry(name, segments));
        }

        entries.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        sw.Stop();
        Log.Info("Index gelesen: {count} Dateien, {ms} ms", entries.Count, sw.ElapsedMilliseconds);
        return new RpaArchiveInfo(archivePath, version, key, entries);
    }

    public void ExtractEntry(string archivePath, RpaEntry entry, string destinationFile)
    {
        using var archive = File.OpenRead(archivePath);
        ExtractEntryCore(archive, entry, destinationFile);
    }

    public byte[]? ReadEntryBytes(string archivePath, RpaEntry entry, long maxBytes)
    {
        if (entry.Size > maxBytes) return null;
        using var archive = File.OpenRead(archivePath);
        using var output = new MemoryStream(capacity: (int)Math.Min(entry.Size, int.MaxValue));
        var buffer = new byte[81920];
        foreach (var seg in entry.Segments)
        {
            if (seg.Prefix.Length > 0)
                output.Write(seg.Prefix, 0, seg.Prefix.Length);

            archive.Seek(seg.Offset, SeekOrigin.Begin);
            long remaining = seg.BytesFromArchive;
            while (remaining > 0)
            {
                int want = (int)Math.Min(remaining, buffer.Length);
                int read = archive.Read(buffer, 0, want);
                if (read <= 0)
                    throw new EndOfStreamException(
                        $"Archiv unerwartet zu Ende beim Lesen von '{entry.Path}'.");
                output.Write(buffer, 0, read);
                remaining -= read;
            }
        }
        return output.ToArray();
    }

    public int Extract(RpaArchiveInfo archive, IEnumerable<RpaEntry> entries, string destinationDirectory,
        IProgress<RpaProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var list = entries.ToList();
        Log.Info("Entpacke {count} Datei(en) nach {dir}", list.Count, destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        string root = Path.GetFullPath(destinationDirectory);

        using var stream = File.OpenRead(archive.ArchivePath);
        int done = 0;
        foreach (var entry in list)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = SafeCombine(root, entry.Path);
            ExtractEntryCore(stream, entry, target);
            done++;
            progress?.Report(new RpaProgress(done, list.Count, entry.Path));
        }
        Log.Info("Entpacken fertig: {count} Datei(en)", done);
        return done;
    }

    public int ExtractAll(RpaArchiveInfo archive, string destinationDirectory,
        IProgress<RpaProgress>? progress = null, CancellationToken cancellationToken = default)
        => Extract(archive, archive.Entries, destinationDirectory, progress, cancellationToken);

    public int Create(string archivePath, string sourceDirectory,
        RpaVersion version = RpaVersion.V3_0, uint key = DefaultKey,
        IProgress<RpaProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (version == RpaVersion.V2_0) key = 0; // 2.0 kennt keinen Key

        var files = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(static f => f, StringComparer.Ordinal)
            .ToList();
        Log.Info("Erstelle {ver}-Archiv {path} aus {count} Datei(en) unter {src}",
            version, archivePath, files.Count, sourceDirectory);

        int headerLen = BuildHeader(version, 0, key).Length;
        string srcRoot = Path.GetFullPath(sourceDirectory);

        using var fs = File.Create(archivePath);
        // Platzhalter fester Länge reservieren — der echte Header wird am Ende geschrieben.
        fs.Write(new byte[headerLen]);

        var index = new Dictionary<string, object>(files.Count);
        var buffer = new byte[81920];
        int done = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string rel = Path.GetRelativePath(srcRoot, Path.GetFullPath(file)).Replace('\\', '/');
            long offset = fs.Position;
            long length;
            using (var input = File.OpenRead(file))
            {
                length = input.Length;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    fs.Write(buffer, 0, read);
            }
            long storedOffset = offset ^ key;
            long storedLength = length ^ key;
            // Einzel-Segment ohne Prefix: [[offset, length]]
            index[rel] = new object[] { new object[] { storedOffset, storedLength } };
            done++;
            progress?.Report(new RpaProgress(done, files.Count, rel));
        }

        long indexOffset = fs.Position;
        byte[] pickled;
        using (var pickler = new Pickler())
            pickled = pickler.dumps(index);
        byte[] compressed = ZlibCompress(pickled);
        fs.Write(compressed);

        // echten Header schreiben (gleiche Länge wie der Platzhalter)
        fs.Seek(0, SeekOrigin.Begin);
        byte[] header = Encoding.ASCII.GetBytes(BuildHeader(version, indexOffset, key));
        if (header.Length != headerLen)
            throw new InvalidOperationException("Header-Länge inkonsistent — interner Fehler.");
        fs.Write(header);

        Log.Info("Archiv erstellt: {count} Datei(en), IndexOffset 0x{off:x}", done, indexOffset);
        return done;
    }

    // ---- intern -------------------------------------------------------------

    private void ExtractEntryCore(Stream archive, RpaEntry entry, string destinationFile)
    {
        string? dir = Path.GetDirectoryName(destinationFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var output = File.Create(destinationFile);
        var buffer = new byte[81920];
        foreach (var seg in entry.Segments)
        {
            if (seg.Prefix.Length > 0)
                output.Write(seg.Prefix, 0, seg.Prefix.Length);

            archive.Seek(seg.Offset, SeekOrigin.Begin);
            long remaining = seg.BytesFromArchive;
            while (remaining > 0)
            {
                int want = (int)Math.Min(remaining, buffer.Length);
                int read = archive.Read(buffer, 0, want);
                if (read <= 0)
                    throw new EndOfStreamException(
                        $"Archiv unerwartet zu Ende beim Entpacken von '{entry.Path}'. Ist die Datei beschädigt?");
                output.Write(buffer, 0, read);
                remaining -= read;
            }
        }
        Log.Trace("Entpackt: {path} ({size} Bytes)", entry.Path, entry.Size);
    }

    private static string BuildHeader(RpaVersion version, long indexOffset, uint key) => version switch
    {
        RpaVersion.V2_0 => $"RPA-2.0 {indexOffset:x16}\n",
        RpaVersion.V3_0 => $"RPA-3.0 {indexOffset:x16} {key:x8}\n",
        RpaVersion.V3_2 => $"RPA-3.2 {indexOffset:x16} 0 {key:x8}\n",
        _ => throw new ArgumentOutOfRangeException(nameof(version)),
    };

    private static string ReadHeaderLine(Stream s)
    {
        var bytes = new List<byte>(64);
        int b;
        while ((b = s.ReadByte()) != -1)
        {
            if (b == '\n') break;
            bytes.Add((byte)b);
            if (bytes.Count > 4096)
                throw new NotSupportedException("Kein gültiger RPA-Header gefunden (keine Zeilenende im Kopfbereich).");
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static long ParseHex(string s) => long.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>Kombiniert Zielordner + Archiv-Pfad und verhindert Ausbrüche (../, absolute Pfade).</summary>
    private static string SafeCombine(string root, string relativePath)
    {
        string cleaned = relativePath.Replace('\\', '/').TrimStart('/');
        var combined = Path.GetFullPath(Path.Combine(root, cleaned));
        string rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootWithSep, StringComparison.Ordinal) &&
            !string.Equals(combined, root, StringComparison.Ordinal))
            throw new IOException($"Archiv-Eintrag '{relativePath}' zeigt aus dem Zielordner heraus — abgelehnt.");
        return combined;
    }

    private static IList<object> AsList(object? o) => o switch
    {
        object[] arr => arr,
        ArrayList al => al.Cast<object>().ToList(),
        IList<object> l => l,
        IList il => il.Cast<object>().ToList(),
        _ => throw new InvalidDataException($"Index-Eintrag hat unerwarteten Typ: {o?.GetType().Name ?? "null"}"),
    };

    private static long ToInt64(object? o) => o switch
    {
        long l => l,
        int i => i,
        uint ui => ui,
        ulong ul => (long)ul,
        short s => s,
        ushort us => us,
        byte b => b,
        sbyte sb => sb,
        BigInteger bi => (long)bi,
        _ => Convert.ToInt64(o, CultureInfo.InvariantCulture),
    };

    private static byte[] ToBytes(object? o) => o switch
    {
        byte[] b => b,
        string s => Encoding.Latin1.GetBytes(s), // Prefix ist byteweise; Latin1 erhält 0..255
        null => [],
        _ => [],
    };

    private static string KeyToPath(object? key) => key switch
    {
        string s => s,
        byte[] b => Encoding.UTF8.GetString(b),
        _ => key?.ToString() ?? throw new InvalidDataException("Index enthält einen Eintrag ohne Namen."),
    };

    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static byte[] ZlibDecompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }
}
