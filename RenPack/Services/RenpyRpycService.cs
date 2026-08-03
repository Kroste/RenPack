using System.Buffers.Binary;
using System.Collections;
using System.IO.Compression;
using System.Text;
using NLog;
using Razorvine.Pickle;

namespace RenPack.Services;

/// <summary>
/// Liest kompilierte Ren'Py-Skripte (<c>.rpyc</c>) und liefert die enthaltene
/// AST-Statement-Liste als Objekt-Netz zurück. Nutzt denselben Catch-all-
/// Unpickler wie <see cref="RenpySaveService"/> — muss nur einmal statisch
/// initialisiert werden, wirkt danach global.
///
/// Format-Referenz (Ren'Py <c>script.py::read_rpyc_data</c>):
///   - Ab Ren'Py 6.99: Header <c>"RENPY RPC2"</c> (10 Bytes ASCII), dann eine
///     Slot-Tabelle mit Tripeln <c>(slot_id, offset, length)</c> aus je 3
///     uint32 (little-endian). Terminator ist <c>slot_id == 0</c>. Slot 1
///     enthält die Bytecode-Daten (zlib-komprimiertes Pickle).
///   - Ältere .rpyc: direkt zlib-komprimiertes Pickle ohne Header.
///
/// Inhalt des Pickles ist ein Tupel <c>(data, statements)</c> — <c>data</c>
/// enthält Metadaten (Ren'Py-Version, ggf. Key), <c>statements</c> ist die
/// AST-Liste. Für die Dekompilierung reichen die Statements.
/// </summary>
public sealed class RenpyRpycService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private const int RpycVersion = 1; // Slot, in dem die Bytecode-Daten liegen.
    private static readonly byte[] MagicRpc2 = "RENPY RPC2"u8.ToArray();

    public RenpyRpycService()
    {
        // Der Save-Service registriert schon den Catch-all-Unpickler; falls
        // dieser Service isoliert (ohne SaveService) genutzt wird, sicherstellen
        // dass er trotzdem greift.
        _ = new RenpySaveService();
    }

    /// <summary>Liest ein <c>.rpyc</c> und liefert die AST-Statements. Wirft bei
    /// kaputten Dateien oder wenn Ren'Py-Klassen im Pickle so verpackt sind,
    /// dass der Catch-all sie nicht deserialisieren kann.</summary>
    public IReadOnlyList<object?> ReadAst(string rpycPath)
    {
        Log.Info("Lese .rpyc: {path}", rpycPath);
        byte[] all = File.ReadAllBytes(rpycPath);
        return ReadAstFromBytes(all);
    }

    public IReadOnlyList<object?> ReadAstFromBytes(byte[] rpycBytes)
    {
        byte[] compressed = ExtractSlotPayload(rpycBytes, RpycVersion);
        byte[] pickle = ZlibDecompress(compressed);

        // Ordered-Dict-Reihenfolge fuer Signature.parameters aus dem Pickle-
        // Byte-Stream extrahieren (Razorvine.Pickle nutzt Hashtable und
        // verliert dabei die Insertion-Order — kritisch fuer label/screen-
        // Parameter, die per Position gebunden werden).
        Queue<List<string>> sigOrder;
        try
        {
            sigOrder = PickleSignatureOrderScanner.Scan(pickle);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Signature-Order-Scanner fehlgeschlagen — Params koennten in falscher Reihenfolge stehen");
            sigOrder = new Queue<List<string>>();
        }

        using var u = new Unpickler();
        var root = u.loads(pickle);
        var stmts = ExtractStatements(root);

        // Signature.parameters in-place patchen mit korrekter Reihenfolge.
        SignatureOrderPatcher.PatchInPlace(stmts, sigOrder);

        return stmts;
    }

    /// <summary>Zieht die Bytes eines Slots aus einem RENPY-RPC2-Container. Bei
    /// Dateien ohne Header wird angenommen, dass die ganze Datei bereits die
    /// zlib-Payload ist (alte Ren'Py-Versionen).</summary>
    private static byte[] ExtractSlotPayload(byte[] all, int slotWanted)
    {
        if (all.Length < MagicRpc2.Length ||
            !all.AsSpan(0, MagicRpc2.Length).SequenceEqual(MagicRpc2))
        {
            return all; // Kein Header — alles ist die Payload.
        }

        int pos = MagicRpc2.Length;
        while (pos + 12 <= all.Length)
        {
            uint slotId = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(pos, 4));
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(pos + 4, 4));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(pos + 8, 4));
            pos += 12;
            if (slotId == 0) break;
            if (slotId == slotWanted)
            {
                if (offset + length > all.Length)
                    throw new InvalidDataException(
                        $"Slot {slotId} zeigt aus der Datei heraus ({offset}+{length} > {all.Length}).");
                return all[(int)offset..(int)(offset + length)];
            }
        }
        throw new InvalidDataException(
            $"Slot {slotWanted} nicht in der Slot-Tabelle gefunden — .rpyc kaputt oder unbekanntes Format.");
    }

    /// <summary>Top-Level ist <c>(data, statements)</c>. Wir extrahieren die
    /// Statements. Toleriert dabei, dass das Root schon direkt eine Liste ist
    /// (sehr alte Formate).</summary>
    private static IReadOnlyList<object?> ExtractStatements(object? root)
    {
        if (root is object?[] { Length: >= 2 } tuple && tuple[1] is IList stmts)
            return stmts.Cast<object?>().ToList();
        if (root is IList direct)
            return direct.Cast<object?>().ToList();
        throw new InvalidDataException(
            ".rpyc-Wurzel hat ein unerwartetes Format — weder (data, statements)-Tupel noch nackte Liste.");
    }

    private static byte[] ZlibDecompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Hilfsmethode für die Dekompilierung: alle .rpyc-Dateien in
    /// einem Ordner rekursiv finden.</summary>
    public static IEnumerable<string> FindRpycFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.rpyc", SearchOption.AllDirectories);
}
