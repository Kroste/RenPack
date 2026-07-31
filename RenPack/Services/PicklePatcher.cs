using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace RenPack.Services;

/// <summary>
/// Byte-preserving Editor für den <c>log</c>-Pickle-Stream eines Ren'Py-Saves.
/// Findet die Positionen der Store-Werte im Bytestrom (per Key-Muster-Scan) und
/// ersetzt sie durch neu gepickelte Werte. Alles außerhalb der geänderten
/// Wert-Bereiche bleibt bit-identisch — insbesondere der komplette
/// RollbackLog-Skeleton und alle unbekannten Ren'Py-Klassen.
///
/// Bewusste Einschränkungen des Scanners:
///   - Es wird nur nach der ERSTEN Vorkommen jedes <c>store.foo</c>-Keys gesucht.
///     Das ist die Zuweisung im roots-Dict; spätere Vorkommen (z. B. in
///     History-Listen) werden ignoriert. Wenn ein Key ausnahmsweise gar nicht
///     als Root-Zuweisung auftaucht, wird er nicht gefunden — Editing dann
///     nicht möglich (Aufrufer bekommt eine Ausnahme).
///   - Nur einfache Werte werden unterstützt (int/float/str/bool/None). Bei
///     komplexen Werten (dict/list/eigene Klasse) wirft der Splice.
/// </summary>
public sealed class PicklePatcher
{
    /// <summary>Sucht die Byte-Position und -Länge eines Werts, der einem
    /// <c>store.&lt;name&gt;</c>-Key im Pickle-Stream folgt.</summary>
    public static ValueSpan FindStoreValue(byte[] pickle, string variableName)
    {
        string qualifiedKey = "store." + variableName;
        byte[] keyBytes = Encoding.UTF8.GetBytes(qualifiedKey);

        int keyPos = FindKeyOccurrence(pickle, keyBytes);
        if (keyPos < 0)
            throw new InvalidOperationException(
                $"Variable '{variableName}' wurde im Pickle-Stream nicht gefunden.");

        // Direkt hinter Key: optional MEMOIZE (0x94), dann der Wert-Opcode.
        int valuePos = keyPos + keyBytes.Length;
        while (valuePos < pickle.Length && pickle[valuePos] == 0x94) valuePos++;

        int valueLen = MeasureValueLength(pickle, valuePos);
        return new ValueSpan(valuePos, valueLen);
    }

    /// <summary>Findet die erste Vorkommen des Store-Keys im Bytestrom als
    /// SHORT_BINUNICODE-Payload (Opcode 0x8C, dann 1-Byte-Länge, dann UTF-8).
    /// Fallback: BINUNICODE (0x58, 4-Byte-Länge) für den seltenen Fall langer Keys.</summary>
    private static int FindKeyOccurrence(byte[] pickle, byte[] keyBytes)
    {
        // SHORT_BINUNICODE-Variante
        for (int i = 0; i < pickle.Length - keyBytes.Length - 2; i++)
        {
            if (pickle[i] != 0x8C) continue;
            if (pickle[i + 1] != keyBytes.Length) continue;
            if (BytesEqual(pickle, i + 2, keyBytes)) return i + 2;
        }
        // BINUNICODE-Variante (selten für Store-Keys, aber möglich)
        for (int i = 0; i < pickle.Length - keyBytes.Length - 5; i++)
        {
            if (pickle[i] != 0x58) continue;
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(pickle.AsSpan(i + 1, 4));
            if (len != (uint)keyBytes.Length) continue;
            if (BytesEqual(pickle, i + 5, keyBytes)) return i + 5;
        }
        return -1;
    }

    private static bool BytesEqual(byte[] arr, int start, byte[] needle)
    {
        if (start + needle.Length > arr.Length) return false;
        for (int i = 0; i < needle.Length; i++)
            if (arr[start + i] != needle[i]) return false;
        return true;
    }

    /// <summary>Bestimmt die Byte-Länge eines Pickle-Werts ab <paramref name="pos"/>.
    /// Nur einfache Typen; wirft bei allem anderen.</summary>
    public static int MeasureValueLength(byte[] pickle, int pos)
    {
        if (pos >= pickle.Length)
            throw new InvalidDataException("Pickle unerwartet zu Ende beim Wert-Parsing.");

        byte op = pickle[pos];
        return op switch
        {
            0x4E => 1, // NONE  'N'
            0x88 => 1, // NEWTRUE
            0x89 => 1, // NEWFALSE
            0x4B => 2, // BININT1 'K' + 1
            0x4D => 3, // BININT2 'M' + 2
            0x4A => 5, // BININT 'J' + 4
            0x8A => 2 + pickle[pos + 1], // LONG1
            0x8B => 5 + (int)BinaryPrimitives.ReadUInt32LittleEndian(pickle.AsSpan(pos + 1, 4)), // LONG4
            0x47 => 9, // BINFLOAT 'G' + 8
            0x8C => 2 + pickle[pos + 1], // SHORT_BINUNICODE
            0x58 => 5 + (int)BinaryPrimitives.ReadUInt32LittleEndian(pickle.AsSpan(pos + 1, 4)), // BINUNICODE
            0x8D => 9 + (int)BinaryPrimitives.ReadUInt64LittleEndian(pickle.AsSpan(pos + 1, 8)), // BINUNICODE8
            _ => throw new NotSupportedException(
                $"Wert-Opcode 0x{op:X2} an Position {pos} wird nicht unterstützt (nur einfache Typen sind editierbar)."),
        };
    }

    /// <summary>Erzeugt Pickle-Bytes für einen einfachen .NET-Wert (int/long,
    /// double, string, bool, null). Wirft bei komplexen Typen.</summary>
    public static byte[] EncodeValue(object? value) => value switch
    {
        null => [0x4E], // NONE
        bool b => [b ? (byte)0x88 : (byte)0x89],
        int i => EncodeLong(i),
        long l => EncodeLong(l),
        double d => EncodeFloat(d),
        float f => EncodeFloat(f),
        string s => EncodeString(s),
        _ => throw new NotSupportedException(
            $"Wert-Typ {value.GetType().Name} kann nicht als einfacher Wert gepickelt werden."),
    };

    private static byte[] EncodeLong(long value)
    {
        // Pickle-Optimierung: kleine positive Werte als BININT1/2/4.
        if (value >= 0 && value <= 0xFF) return [0x4B, (byte)value]; // BININT1
        if (value >= 0 && value <= 0xFFFF) return [0x4D, (byte)value, (byte)(value >> 8)]; // BININT2
        if (value >= int.MinValue && value <= int.MaxValue)
        {
            var buf = new byte[5];
            buf[0] = 0x4A; // BININT
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(1), (int)value);
            return buf;
        }
        // Größere Werte als LONG1 (little-endian two's complement, minimum bytes)
        return EncodeLongBig(value);
    }

    private static byte[] EncodeLongBig(long value)
    {
        var raw = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(raw, value);
        int len = 8;
        if (value >= 0)
            while (len > 1 && raw[len - 1] == 0x00 && (raw[len - 2] & 0x80) == 0) len--;
        else
            while (len > 1 && raw[len - 1] == 0xFF && (raw[len - 2] & 0x80) != 0) len--;
        var result = new byte[2 + len];
        result[0] = 0x8A; // LONG1
        result[1] = (byte)len;
        Buffer.BlockCopy(raw, 0, result, 2, len);
        return result;
    }

    private static byte[] EncodeFloat(double value)
    {
        var buf = new byte[9];
        buf[0] = 0x47; // BINFLOAT 'G'
        BinaryPrimitives.WriteDoubleBigEndian(buf.AsSpan(1), value);
        return buf;
    }

    private static byte[] EncodeString(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        if (utf8.Length < 256)
        {
            var buf = new byte[2 + utf8.Length];
            buf[0] = 0x8C; // SHORT_BINUNICODE
            buf[1] = (byte)utf8.Length;
            Buffer.BlockCopy(utf8, 0, buf, 2, utf8.Length);
            return buf;
        }
        var big = new byte[5 + utf8.Length];
        big[0] = 0x58; // BINUNICODE
        BinaryPrimitives.WriteUInt32LittleEndian(big.AsSpan(1), (uint)utf8.Length);
        Buffer.BlockCopy(utf8, 0, big, 5, utf8.Length);
        return big;
    }

    /// <summary>Wendet mehrere Wert-Ersetzungen auf den Pickle-Stream an.
    /// Reihenfolge egal — die Methode sortiert intern nach Position (rückwärts),
    /// sodass frühere Positionen durch spätere Splices nicht verschoben werden.
    /// Wichtig: Pickle-Streams ab Protocol 4 verwenden FRAME-Opcodes mit
    /// Längenangabe — diese werden aktualisiert, sofern ein Splice innerhalb
    /// eines Frames liegt.</summary>
    public static byte[] Splice(byte[] original, IReadOnlyList<PatchOp> patches)
    {
        if (patches.Count == 0) return original;

        var sorted = patches.OrderByDescending(p => p.Position).ToArray();

        // Frame-Längen (Opcode 0x95 = FRAME, dann uint64-Länge) neu berechnen.
        var frames = ScanFrames(original);
        var frameDeltas = new Dictionary<int, long>();
        foreach (var p in sorted)
        {
            long delta = p.NewBytes.Length - p.OldLength;
            if (delta == 0) continue;
            foreach (var f in frames)
                if (p.Position >= f.PayloadStart && p.Position + p.OldLength <= f.PayloadStart + f.PayloadLength)
                {
                    frameDeltas.TryGetValue(f.HeaderPos, out var existing);
                    frameDeltas[f.HeaderPos] = existing + delta;
                }
        }

        using var ms = new MemoryStream(original.Length + patches.Sum(p => p.NewBytes.Length));
        int cursor = 0;
        var patchQueue = sorted.OrderBy(p => p.Position).ToArray();

        foreach (var p in patchQueue)
        {
            // Bytes vor dem Splice übernehmen — dabei Frame-Header ggf. neu schreiben.
            WriteRange(ms, original, cursor, p.Position - cursor, frames, frameDeltas);
            ms.Write(p.NewBytes);
            cursor = p.Position + p.OldLength;
        }
        WriteRange(ms, original, cursor, original.Length - cursor, frames, frameDeltas);
        return ms.ToArray();
    }

    private static void WriteRange(MemoryStream dst, byte[] src, int start, int length,
        IReadOnlyList<FrameSpan> frames, IReadOnlyDictionary<int, long> deltas)
    {
        int end = start + length;
        int i = start;
        var frameBuf = new byte[8];
        while (i < end)
        {
            var frame = frames.FirstOrDefault(f => f.HeaderPos == i);
            if (frame is not null && i + 9 <= end)
            {
                long newLen = frame.PayloadLength + (deltas.TryGetValue(i, out var d) ? d : 0);
                dst.WriteByte(0x95);
                BinaryPrimitives.WriteInt64LittleEndian(frameBuf, newLen);
                dst.Write(frameBuf);
                i += 9;
            }
            else
            {
                int next = Math.Min(end, frames.Where(f => f.HeaderPos > i).Select(f => f.HeaderPos).DefaultIfEmpty(end).Min());
                dst.Write(src, i, next - i);
                i = next;
            }
        }
    }

    private static List<FrameSpan> ScanFrames(byte[] pickle)
    {
        var list = new List<FrameSpan>();
        int i = 0;
        while (i < pickle.Length - 9)
        {
            if (pickle[i] != 0x95) { i++; continue; }
            long len = BinaryPrimitives.ReadInt64LittleEndian(pickle.AsSpan(i + 1, 8));
            if (len < 0 || i + 9 + len > pickle.Length) { i++; continue; }
            list.Add(new FrameSpan(i, i + 9, len));
            i += 9 + (int)len;
        }
        return list;
    }

    public readonly record struct ValueSpan(int Position, int Length);
    public sealed record PatchOp(int Position, int OldLength, byte[] NewBytes);
    private sealed record FrameSpan(int HeaderPos, int PayloadStart, long PayloadLength);
}
