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
        // Fuer Container (list/dict/tuple) walken wir opcode-basiert und
        // enden wenn der Netto-Stack-Effekt +1 erreicht ist (wir haben genau
        // einen Wert auf den Stack gepusht).
        if (op is 0x5D or 0x7D or 0x29 or 0x28 or 0x8F or 0x64 or 0x6C)
            return MeasureContainerLength(pickle, pos);

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
            0x68 => 2, // BINGET (memo ref counts as 1 value)
            0x6A => 5, // LONG_BINGET
            _ => throw new NotSupportedException(
                $"Wert-Opcode 0x{op:X2} an Position {pos} wird nicht unterstützt."),
        };
    }

    /// <summary>Misst die Byte-Laenge eines Container-Werts (Liste/Dict/Tuple)
    /// per Opcode-Walk mit echter Stack-Groessen- und Mark-Position-Tracking.
    /// Startet bei EMPTY_LIST/EMPTY_DICT/EMPTY_TUPLE/MARK-Instruktion und
    /// endet, wenn der Netto-Effekt "genau ein Wert wurde gepusht" ist —
    /// der fertige Container liegt dann auf dem Stack.
    ///
    /// Wichtig fuer geschachtelte Container: wir tracken ECHT die MARK-
    /// Positionen im Stack, damit `SETITEMS` beim inneren Dict den inneren
    /// Bereich abraeumt und der aeussere weiter aufgebaut wird.</summary>
    private static int MeasureContainerLength(byte[] pickle, int startPos)
    {
        int pos = startPos;
        int stackSize = 0;
        var markStack = new Stack<int>();
        bool sawTerminator = false;

        while (pos < pickle.Length)
        {
            byte op = pickle[pos];
            pos = StepOpcode(pickle, pos, ref stackSize, markStack);
            if (IsTerminatorOp(op)) sawTerminator = true;

            // Fertig-Kriterium: stackSize==1 && markStack leer.
            // Aber Vorsicht: nach EMPTY_LIST/EMPTY_DICT ist das schon nach dem
            // ersten Opcode wahr — der Container koennte aber noch per SETITEM
            // /APPEND / MARK+SETITEMS gefuellt werden. Zwei Faelle:
            //   1. Wir haben schon einen Terminator gesehen → wirklich fertig.
            //   2. Noch kein Terminator → peek naechsten "echten" Opcode
            //      (MEMOIZE/BINPUT ueberspringen). Wenn's ein Fill-Op ist
            //      (MARK/APPEND/SETITEM), weitermachen; sonst fertig.
            if (stackSize == 1 && markStack.Count == 0)
            {
                if (sawTerminator) return pos - startPos;
                int peekPos = SkipMemoOps(pickle, pos);
                if (peekPos >= pickle.Length || !IsFillOp(pickle[peekPos]))
                    return pos - startPos;
            }
        }
        throw new InvalidDataException(
            $"Container-Wert bei Position {startPos} nicht abgeschlossen (Ende erreicht).");
    }

    /// <summary>Ueberspringt MEMOIZE/BINPUT/LONG_BINPUT-Opcodes (die den
    /// Stack-Top nicht aendern) und liefert die Position des naechsten
    /// nicht-Memoize-Opcodes.</summary>
    private static int SkipMemoOps(byte[] pickle, int pos)
    {
        while (pos < pickle.Length)
        {
            byte op = pickle[pos];
            if (op == 0x94) { pos++; continue; }              // MEMOIZE
            if (op == 0x71) { pos += 2; continue; }           // BINPUT
            if (op == 0x72) { pos += 5; continue; }           // LONG_BINPUT
            break;
        }
        return pos;
    }

    /// <summary>Opcodes die den Container am Stack-Top erweitern —
    /// nach EMPTY_LIST/EMPTY_DICT sind das die Fortsetzungs-Signale.</summary>
    private static bool IsFillOp(byte op) => op is
        0x28  // MARK (fuer folgendes APPENDS/SETITEMS)
        or 0x61  // APPEND (Single-List-Item)
        or 0x73; // SETITEM (Single-Dict-Pair)

    /// <summary>Opcodes die einen Container abschliessen — nach einem
    /// Terminator und stackSize==1 sind wir garantiert fertig.</summary>
    private static bool IsTerminatorOp(byte op) => op is
        0x61  // APPEND
        or 0x65  // APPENDS
        or 0x73  // SETITEM
        or 0x75  // SETITEMS
        or 0x74  // TUPLE
        or 0x64  // DICT
        or 0x6C  // LIST
        or 0x85 or 0x86 or 0x87  // TUPLE1/2/3
        or 0x90; // ADDITEMS

    /// <summary>Verarbeitet ein Opcode: liefert neue Position, aktualisiert
    /// <paramref name="stackSize"/> und <paramref name="markStack"/>.</summary>
    private static int StepOpcode(byte[] pickle, int pos, ref int stackSize,
        Stack<int> markStack)
    {
        byte op = pickle[pos++];
        switch (op)
        {
            // --- Push scalars ---
            case 0x4E:                                            // NONE
            case 0x88: case 0x89:                                 // NEWTRUE/NEWFALSE
                stackSize++; return pos;
            case 0x4B: stackSize++; return pos + 1;               // BININT1
            case 0x4D: stackSize++; return pos + 2;               // BININT2
            case 0x4A: stackSize++; return pos + 4;               // BININT
            case 0x8A: { int n = pickle[pos]; stackSize++; return pos + 1 + n; }
            case 0x8B: { int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(pickle.AsSpan(pos, 4)); stackSize++; return pos + 4 + n; }
            case 0x47: stackSize++; return pos + 8;               // BINFLOAT
            case 0x8C: { int n = pickle[pos]; stackSize++; return pos + 1 + n; }
            case 0x58: { int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(pickle.AsSpan(pos, 4)); stackSize++; return pos + 4 + n; }
            case 0x8D: { int n = (int)BinaryPrimitives.ReadUInt64LittleEndian(pickle.AsSpan(pos, 8)); stackSize++; return pos + 8 + n; }
            case 0x68: stackSize++; return pos + 1;               // BINGET
            case 0x6A: stackSize++; return pos + 4;               // LONG_BINGET

            // --- Memoize (no stack change) ---
            case 0x94: return pos;                                // MEMOIZE
            case 0x71: return pos + 1;                            // BINPUT
            case 0x72: return pos + 4;                            // LONG_BINPUT

            // --- Empty containers ---
            case 0x29: stackSize++; return pos;                   // EMPTY_TUPLE
            case 0x5D: stackSize++; return pos;                   // EMPTY_LIST
            case 0x7D: stackSize++; return pos;                   // EMPTY_DICT
            case 0x8F: stackSize++; return pos;                   // EMPTY_SET

            // --- MARK ---
            case 0x28:
                markStack.Push(stackSize);
                stackSize++;
                return pos;

            // --- Tuple-shortcuts ---
            case 0x85: return pos;                                // TUPLE1 (pop 1 push 1)
            case 0x86: stackSize--; return pos;                   // TUPLE2 (pop 2 push 1)
            case 0x87: stackSize -= 2; return pos;                // TUPLE3 (pop 3 push 1)

            // --- Mark-based container-close ---
            case 0x74:                                            // TUPLE
            case 0x64:                                            // DICT
            case 0x6C:                                            // LIST
                stackSize = markStack.Pop();                      // pop MARK + all above
                stackSize++;                                      // push new container
                return pos;

            // --- List/Set append ---
            case 0x61: stackSize--; return pos;                   // APPEND (pop 1)
            case 0x65:                                            // APPENDS
            case 0x90:                                            // ADDITEMS
                stackSize = markStack.Pop();                      // list stays untouched below
                return pos;

            // --- Dict setitem ---
            case 0x73: stackSize -= 2; return pos;                // SETITEM (pop k, v)
            case 0x75:                                            // SETITEMS
                stackSize = markStack.Pop();                      // dict stays below
                return pos;

            case 0x2E: return pos;                                // STOP

            default:
                throw new NotSupportedException(
                    $"MeasureContainerLength: Opcode 0x{op:X2} an Position {pos - 1} nicht unterstuetzt " +
                    $"(Wert enthaelt vermutlich Custom-Klassen — nicht editierbar).");
        }
    }

    /// <summary>Erzeugt Pickle-Bytes für einen .NET-Wert. Unterstuetzt einfache
    /// Typen (int/long, double, string, bool, null) sowie flat und verschachtelte
    /// Listen/Dicts/Tuples (v0.5). Wirft bei nicht unterstuetzten Typen (Custom-
    /// Klassen, ClassDict, etc.).</summary>
    public static byte[] EncodeValue(object? value)
    {
        var sb = new List<byte>(64);
        EncodeValueInto(sb, value);
        return sb.ToArray();
    }

    private static void EncodeValueInto(List<byte> sb, object? value)
    {
        switch (value)
        {
            case null: sb.Add(0x4E); break;
            case bool b: sb.Add(b ? (byte)0x88 : (byte)0x89); break;
            case int i: sb.AddRange(EncodeLong(i)); break;
            case long l: sb.AddRange(EncodeLong(l)); break;
            case short s16: sb.AddRange(EncodeLong(s16)); break;
            case byte u8: sb.AddRange(EncodeLong(u8)); break;
            case double d: sb.AddRange(EncodeFloat(d)); break;
            case float f: sb.AddRange(EncodeFloat(f)); break;
            case string str: sb.AddRange(EncodeString(str)); break;
            case System.Collections.IDictionary dict: EncodeDict(sb, dict); break;
            case object?[] tuple: EncodeTuple(sb, tuple); break;
            case System.Collections.IEnumerable list: EncodeList(sb, list); break;
            default:
                throw new NotSupportedException(
                    $"Wert-Typ {value.GetType().Name} kann nicht als Pickle-Wert enkodiert werden.");
        }
    }

    /// <summary>Emittiert <c>EMPTY_LIST MARK item* APPENDS</c> — passend zu
    /// Python's Standard-Serialisierung von nicht-leeren Listen. Immer mit
    /// MARK+APPENDS statt Single-Item-APPEND — so kann <see cref="MeasureContainerLength"/>
    /// eindeutig terminieren (ohne Look-Ahead auf potentielle Nachbar-Werte).</summary>
    private static void EncodeList(List<byte> sb, System.Collections.IEnumerable items)
    {
        sb.Add(0x5D); // EMPTY_LIST
        var buffered = items.Cast<object?>().ToList();
        if (buffered.Count == 0) return;
        sb.Add(0x28); // MARK
        foreach (var item in buffered) EncodeValueInto(sb, item);
        sb.Add(0x65); // APPENDS
    }

    /// <summary>Emittiert <c>EMPTY_DICT MARK (key, value)+ SETITEMS</c> —
    /// passend zu Python's Standard-Serialisierung. Immer mit MARK+SETITEMS
    /// statt Single-Item-SETITEM — siehe <see cref="EncodeList"/>.</summary>
    private static void EncodeDict(List<byte> sb, System.Collections.IDictionary dict)
    {
        sb.Add(0x7D); // EMPTY_DICT
        if (dict.Count == 0) return;
        sb.Add(0x28); // MARK
        foreach (System.Collections.DictionaryEntry e in dict)
        {
            EncodeValueInto(sb, e.Key);
            EncodeValueInto(sb, e.Value);
        }
        sb.Add(0x75); // SETITEMS
    }

    /// <summary>Emittiert Tuple. Fuer 0/1/2/3-Element-Tuples nutzt Python
    /// EMPTY_TUPLE/TUPLE1/TUPLE2/TUPLE3-Kurzformen, groessere per MARK...TUPLE.</summary>
    private static void EncodeTuple(List<byte> sb, object?[] items)
    {
        switch (items.Length)
        {
            case 0:
                sb.Add(0x29); // EMPTY_TUPLE
                return;
            case 1:
                EncodeValueInto(sb, items[0]);
                sb.Add(0x85); // TUPLE1
                return;
            case 2:
                EncodeValueInto(sb, items[0]);
                EncodeValueInto(sb, items[1]);
                sb.Add(0x86); // TUPLE2
                return;
            case 3:
                EncodeValueInto(sb, items[0]);
                EncodeValueInto(sb, items[1]);
                EncodeValueInto(sb, items[2]);
                sb.Add(0x87); // TUPLE3
                return;
            default:
                sb.Add(0x28); // MARK
                foreach (var item in items) EncodeValueInto(sb, item);
                sb.Add(0x74); // TUPLE
                return;
        }
    }

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
