using System.Buffers.Binary;
using System.Text;

namespace RenPack.Services;

/// <summary>
/// Mini-Pickle-VM zum Extrahieren der Insertion-Order von
/// <c>renpy.parameter.Signature.parameters</c>-Dicts.
///
/// Razorvine.Pickle materialisiert Python-Dicts als <see cref="System.Collections.Hashtable"/>,
/// dessen Iteration in Hash-Bucket-Order laeuft. Ren'Py's Signature speichert
/// die Label/Screen-Parameter aber als OrderedDict (Insertion-Order = Quellcode-
/// Reihenfolge). Ergebnis: der dekompilierte <c>label X(a, b)</c> wird zu
/// <c>label X(b, a)</c> — mit Positional-Bindung des Callers <c>call X(1, 2)</c>
/// dann falsche Params. Verifiziert an Interview Desires 0.23:
/// <c>label try_unlock_truth(index, unlocked_list)</c> muesste sein
/// <c>(unlocked_list, index)</c>.
///
/// Fix: wir scannen die Pickle-Bytes ein zweites Mal mit einem winzigen
/// eigenen Interpreter, der NUR das noetige Minimum aus dem Pickle-Protokoll
/// versteht, um Signature-BUILD-Events zu erkennen und die Insertion-Order
/// der Sub-Dict-Keys herauszuziehen.
///
/// Rueckgabe ist eine <see cref="Queue{T}"/> aller Signature-Param-Namen in
/// pickle-DFS-Order — der Decompiler dequeueed pro AST-Signature einen Eintrag.
///
/// Klassen die wir tracken: nur die Signature-Kette. Andere Objekte werden
/// als <c>null</c>/generisches <see cref="Instance"/> gepusht, damit die
/// Stack-Balance stimmt aber ihre Struktur uns nicht kuemmert.
/// </summary>
public static class PickleSignatureOrderScanner
{
    private const string SignatureClassName = "Signature";
    private const string SignatureModuleName = "renpy.parameter";
    private const string ParametersFieldName = "parameters";

    private static readonly object MarkSentinel = new();

    /// <summary>Class-Ref-Marker: STACK_GLOBAL/GLOBAL laed hiermit eine
    /// Klassen-Referenz auf den Stack. NEWOBJ liest ihn spaeter, um zu
    /// wissen welche Klasse instanziiert wird.</summary>
    private sealed record ClassRef(string Module, string Name);

    /// <summary>Marker fuer ein per NEWOBJ/REDUCE erzeugtes Objekt. Wenn
    /// spaeter BUILD auf dieses Objekt angewendet wird und die Klasse
    /// <c>renpy.parameter.Signature</c> ist, extrahieren wir die Param-Order
    /// aus dem State.</summary>
    private sealed class Instance
    {
        public required ClassRef Class { get; init; }
    }

    /// <summary>Marker fuer einen Dict — sammelt (Key, Value)-Paare in
    /// Insertion-Order. Values koennen andere <see cref="OrderedDictMarker"/>-
    /// Instanzen oder beliebige Objekte sein.</summary>
    private sealed class OrderedDictMarker
    {
        public List<KeyValuePair<object?, object?>> Entries { get; } = new();
        public object? Get(string key)
        {
            foreach (var kv in Entries)
                if (kv.Key is string s && s == key) return kv.Value;
            return null;
        }
        public List<string> StringKeys()
        {
            var result = new List<string>(Entries.Count);
            foreach (var kv in Entries)
                if (kv.Key is string s) result.Add(s);
            return result;
        }
    }

    /// <summary>Tuple-Marker mit den enthaltenen Elementen (fuer TUPLE1/2/3/TUPLE).</summary>
    private sealed class TupleMarker
    {
        public required object?[] Items { get; init; }
    }

    public static Queue<List<string>> Scan(byte[] pickle)
    {
        var stack = new List<object?>(64);
        var memo = new Dictionary<int, object?>(256);
        int nextMemoAuto = 0;
        var result = new Queue<List<string>>();
        int pos = 0;

        while (pos < pickle.Length)
        {
            byte op = pickle[pos++];
            switch (op)
            {
                case 0x28: // MARK
                    stack.Add(MarkSentinel);
                    break;
                case 0x2E: // STOP
                    return result;
                case 0x30: // POP
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    break;
                case 0x31: // POP_MARK
                    PopToMark(stack);
                    break;
                case 0x32: // DUP
                    stack.Add(stack[^1]);
                    break;

                // === Push scalars ===
                case 0x4E: // NONE
                    stack.Add(null); break;
                case 0x88: // NEWTRUE
                    stack.Add(true); break;
                case 0x89: // NEWFALSE
                    stack.Add(false); break;

                // === Ints ===
                case 0x49: // INT (text, \n-term)
                    stack.Add(null); pos = SkipUntilNewline(pickle, pos); break;
                case 0x4A: // BININT (4-byte signed)
                    stack.Add(null); pos += 4; break;
                case 0x4B: // BININT1
                    stack.Add(null); pos += 1; break;
                case 0x4D: // BININT2
                    stack.Add(null); pos += 2; break;
                case 0x4C: // LONG (text)
                    stack.Add(null); pos = SkipUntilNewline(pickle, pos); break;
                case 0x8A: // LONG1
                    {
                        int len = pickle[pos++]; pos += len;
                        stack.Add(null);
                    }
                    break;
                case 0x8B: // LONG4
                    {
                        int len = BinaryPrimitives.ReadInt32LittleEndian(pickle.AsSpan(pos, 4));
                        pos += 4; pos += len;
                        stack.Add(null);
                    }
                    break;

                // === Floats ===
                case 0x46: // FLOAT (text)
                    stack.Add(null); pos = SkipUntilNewline(pickle, pos); break;
                case 0x47: // BINFLOAT (8 bytes big-endian)
                    stack.Add(null); pos += 8; break;

                // === Strings (bytes / ascii) ===
                case 0x53: // STRING (text, quoted, \n-term)
                    stack.Add(null); pos = SkipUntilNewline(pickle, pos); break;
                case 0x54: // BINSTRING (4-byte len, ascii)
                    {
                        int len = BinaryPrimitives.ReadInt32LittleEndian(pickle.AsSpan(pos, 4));
                        pos += 4;
                        stack.Add(Encoding.ASCII.GetString(pickle, pos, len));
                        pos += len;
                    }
                    break;
                case 0x55: // SHORT_BINSTRING
                    {
                        int len = pickle[pos++];
                        stack.Add(Encoding.ASCII.GetString(pickle, pos, len));
                        pos += len;
                    }
                    break;

                // === Bytes ===
                case 0x42: // BINBYTES (4-byte len)
                    {
                        int len = BinaryPrimitives.ReadInt32LittleEndian(pickle.AsSpan(pos, 4));
                        pos += 4; pos += len;
                        stack.Add(null);
                    }
                    break;
                case 0x43: // SHORT_BINBYTES
                    {
                        int len = pickle[pos++]; pos += len;
                        stack.Add(null);
                    }
                    break;
                case 0x8E: // BINBYTES8 (8-byte len)
                    {
                        long len = BinaryPrimitives.ReadInt64LittleEndian(pickle.AsSpan(pos, 8));
                        pos += 8; pos += (int)len;
                        stack.Add(null);
                    }
                    break;
                case 0x96: // BYTEARRAY8
                    {
                        long len = BinaryPrimitives.ReadInt64LittleEndian(pickle.AsSpan(pos, 8));
                        pos += 8; pos += (int)len;
                        stack.Add(null);
                    }
                    break;

                // === Unicode ===
                case 0x56: // UNICODE (text, escaped, \n-term)
                    stack.Add(null); pos = SkipUntilNewline(pickle, pos); break;
                case 0x58: // BINUNICODE (4-byte len utf8)
                    {
                        int len = BinaryPrimitives.ReadInt32LittleEndian(pickle.AsSpan(pos, 4));
                        pos += 4;
                        stack.Add(Encoding.UTF8.GetString(pickle, pos, len));
                        pos += len;
                    }
                    break;
                case 0x8C: // SHORT_BINUNICODE
                    {
                        int len = pickle[pos++];
                        stack.Add(Encoding.UTF8.GetString(pickle, pos, len));
                        pos += len;
                    }
                    break;
                case 0x8D: // BINUNICODE8 (8-byte len utf8)
                    {
                        long len = BinaryPrimitives.ReadInt64LittleEndian(pickle.AsSpan(pos, 8));
                        pos += 8;
                        stack.Add(Encoding.UTF8.GetString(pickle, pos, (int)len));
                        pos += (int)len;
                    }
                    break;

                // === Containers ===
                case 0x29: // EMPTY_TUPLE
                    stack.Add(new TupleMarker { Items = Array.Empty<object?>() });
                    break;
                case 0x5D: // EMPTY_LIST
                    stack.Add(null); break;
                case 0x7D: // EMPTY_DICT
                    stack.Add(new OrderedDictMarker()); break;
                case 0x8F: // EMPTY_SET
                    stack.Add(null); break;
                case 0x91: // FROZENSET
                    PopToMark(stack); stack.Add(null); break;
                case 0x74: // TUPLE
                    {
                        var items = PopToMark(stack);
                        stack.Add(new TupleMarker { Items = items });
                    }
                    break;
                case 0x85: // TUPLE1
                    {
                        var v = stack[^1]; stack.RemoveAt(stack.Count - 1);
                        stack.Add(new TupleMarker { Items = new[] { v } });
                    }
                    break;
                case 0x86: // TUPLE2
                    {
                        var b = stack[^1]; stack.RemoveAt(stack.Count - 1);
                        var a = stack[^1]; stack.RemoveAt(stack.Count - 1);
                        stack.Add(new TupleMarker { Items = new[] { a, b } });
                    }
                    break;
                case 0x87: // TUPLE3
                    {
                        var c = stack[^1]; stack.RemoveAt(stack.Count - 1);
                        var b = stack[^1]; stack.RemoveAt(stack.Count - 1);
                        var a = stack[^1]; stack.RemoveAt(stack.Count - 1);
                        stack.Add(new TupleMarker { Items = new[] { a, b, c } });
                    }
                    break;
                case 0x6C: // LIST (mark-based)
                    PopToMark(stack); stack.Add(null); break;
                case 0x64: // DICT (mark-based) — pairs (k, v, k, v, ...)
                    {
                        var items = PopToMark(stack);
                        var d = new OrderedDictMarker();
                        for (int i = 0; i + 1 < items.Length; i += 2)
                            d.Entries.Add(new KeyValuePair<object?, object?>(items[i], items[i + 1]));
                        stack.Add(d);
                    }
                    break;

                // === List / Set operations (Stack sauber halten) ===
                case 0x61: // APPEND — pop v, list-top stays
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    break;
                case 0x65: // APPENDS — pop items back to mark
                    PopToMark(stack);
                    break;
                case 0x90: // ADDITEMS
                    PopToMark(stack);
                    break;

                // === Dict operations ===
                case 0x73: // SETITEM — pop v, pop k, top is dict
                    {
                        var v = stack[^1]; stack.RemoveAt(stack.Count - 1);
                        var k = stack[^1]; stack.RemoveAt(stack.Count - 1);
                        if (stack[^1] is OrderedDictMarker d)
                            d.Entries.Add(new KeyValuePair<object?, object?>(k, v));
                    }
                    break;
                case 0x75: // SETITEMS — pop items back to mark, apply pairs
                    {
                        var items = PopToMark(stack);
                        if (stack[^1] is OrderedDictMarker d)
                        {
                            for (int i = 0; i + 1 < items.Length; i += 2)
                                d.Entries.Add(new KeyValuePair<object?, object?>(items[i], items[i + 1]));
                        }
                    }
                    break;

                // === Memo ===
                case 0x67: // GET (text)
                    {
                        int end = FindNewline(pickle, pos);
                        int idx = int.Parse(Encoding.ASCII.GetString(pickle, pos, end - pos));
                        pos = end + 1;
                        stack.Add(memo.TryGetValue(idx, out var v) ? v : null);
                    }
                    break;
                case 0x68: // BINGET (1-byte)
                    {
                        int idx = pickle[pos++];
                        stack.Add(memo.TryGetValue(idx, out var v) ? v : null);
                    }
                    break;
                case 0x6A: // LONG_BINGET (4-byte)
                    {
                        int idx = BinaryPrimitives.ReadInt32LittleEndian(pickle.AsSpan(pos, 4));
                        pos += 4;
                        stack.Add(memo.TryGetValue(idx, out var v) ? v : null);
                    }
                    break;
                case 0x70: // PUT (text)
                    {
                        int end = FindNewline(pickle, pos);
                        int idx = int.Parse(Encoding.ASCII.GetString(pickle, pos, end - pos));
                        pos = end + 1;
                        memo[idx] = stack[^1];
                    }
                    break;
                case 0x71: // BINPUT
                    memo[pickle[pos++]] = stack[^1];
                    break;
                case 0x72: // LONG_BINPUT
                    {
                        int idx = BinaryPrimitives.ReadInt32LittleEndian(pickle.AsSpan(pos, 4));
                        pos += 4;
                        memo[idx] = stack[^1];
                    }
                    break;
                case 0x94: // MEMOIZE
                    memo[nextMemoAuto++] = stack[^1];
                    break;

                // === Class / Object ===
                case 0x63: // GLOBAL (module\nname\n)
                    {
                        int end1 = FindNewline(pickle, pos);
                        string mod = Encoding.UTF8.GetString(pickle, pos, end1 - pos);
                        pos = end1 + 1;
                        int end2 = FindNewline(pickle, pos);
                        string name = Encoding.UTF8.GetString(pickle, pos, end2 - pos);
                        pos = end2 + 1;
                        stack.Add(new ClassRef(mod, name));
                    }
                    break;
                case 0x93: // STACK_GLOBAL — pop name, pop module, push ref
                    {
                        var name = stack[^1] as string; stack.RemoveAt(stack.Count - 1);
                        var mod = stack[^1] as string; stack.RemoveAt(stack.Count - 1);
                        stack.Add(new ClassRef(mod ?? "", name ?? ""));
                    }
                    break;
                case 0x69: // INST (module\nname\n, then args from mark)
                    {
                        int end1 = FindNewline(pickle, pos);
                        string mod = Encoding.UTF8.GetString(pickle, pos, end1 - pos);
                        pos = end1 + 1;
                        int end2 = FindNewline(pickle, pos);
                        string name = Encoding.UTF8.GetString(pickle, pos, end2 - pos);
                        pos = end2 + 1;
                        PopToMark(stack); // args
                        stack.Add(new Instance { Class = new ClassRef(mod, name) });
                    }
                    break;
                case 0x6F: // OBJ (mark, class, args → REDUCE)
                    {
                        var items = PopToMark(stack);
                        var cls = items.Length > 0 ? items[0] as ClassRef : null;
                        stack.Add(cls is not null
                            ? new Instance { Class = cls }
                            : (object?)null);
                    }
                    break;
                case 0x52: // REDUCE — pop args, pop callable, push result
                    {
                        stack.RemoveAt(stack.Count - 1); // args
                        var cls = stack[^1] as ClassRef;
                        stack.RemoveAt(stack.Count - 1);
                        stack.Add(cls is not null
                            ? new Instance { Class = cls }
                            : (object?)null);
                    }
                    break;
                case 0x81: // NEWOBJ — pop args, pop class, push instance
                    {
                        stack.RemoveAt(stack.Count - 1); // args
                        var cls = stack[^1] as ClassRef;
                        stack.RemoveAt(stack.Count - 1);
                        stack.Add(cls is not null
                            ? new Instance { Class = cls }
                            : (object?)null);
                    }
                    break;
                case 0x92: // NEWOBJ_EX — pop kwargs, args, class
                    {
                        stack.RemoveAt(stack.Count - 1); // kwargs
                        stack.RemoveAt(stack.Count - 1); // args
                        var cls = stack[^1] as ClassRef;
                        stack.RemoveAt(stack.Count - 1);
                        stack.Add(cls is not null
                            ? new Instance { Class = cls }
                            : (object?)null);
                    }
                    break;
                case 0x62: // BUILD — pop state, apply to top
                    {
                        var state = stack[^1]; stack.RemoveAt(stack.Count - 1);
                        var obj = stack[^1];
                        if (obj is Instance inst
                            && inst.Class.Name == SignatureClassName
                            && inst.Class.Module == SignatureModuleName)
                        {
                            // State ist typischerweise (None, {parameters: <dict>})
                            // — wir suchen den <dict>-Value nach dem "parameters"-
                            // Key und extrahieren dessen Insertion-Order.
                            OrderedDictMarker? stateDict = state switch
                            {
                                TupleMarker t when t.Items.Length >= 2 => t.Items[1] as OrderedDictMarker,
                                OrderedDictMarker d => d,
                                _ => null,
                            };
                            if (stateDict?.Get(ParametersFieldName) is OrderedDictMarker paramsDict)
                                result.Enqueue(paramsDict.StringKeys());
                        }
                    }
                    break;

                // === Persistent IDs ===
                case 0x50: // PERSID (text)
                    stack.Add(null); pos = SkipUntilNewline(pickle, pos); break;
                case 0x51: // BINPERSID
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    stack.Add(null);
                    break;

                // === Extensions ===
                case 0x82: pos += 1; stack.Add(null); break; // EXT1
                case 0x83: pos += 2; stack.Add(null); break; // EXT2
                case 0x84: pos += 4; stack.Add(null); break; // EXT4

                // === Protocol / Frame ===
                case 0x80: // PROTO
                    pos++; break;
                case 0x95: // FRAME
                    pos += 8; break;

                // === Out-of-band buffers (proto 5) ===
                case 0x97: // NEXT_BUFFER
                    stack.Add(null); break;
                case 0x98: // READONLY_BUFFER
                    break;

                default:
                    throw new InvalidDataException(
                        $"PickleSignatureOrderScanner: Unbekannter Opcode 0x{op:X2} an Position {pos - 1}");
            }
        }
        return result;
    }

    private static object?[] PopToMark(List<object?> stack)
    {
        int i = stack.Count - 1;
        while (i >= 0 && !ReferenceEquals(stack[i], MarkSentinel)) i--;
        if (i < 0) return Array.Empty<object?>();
        int count = stack.Count - i - 1;
        var arr = new object?[count];
        for (int j = 0; j < count; j++) arr[j] = stack[i + 1 + j];
        stack.RemoveRange(i, stack.Count - i);
        return arr;
    }

    private static int SkipUntilNewline(byte[] data, int pos)
    {
        while (pos < data.Length && data[pos] != (byte)'\n') pos++;
        return pos < data.Length ? pos + 1 : pos;
    }

    private static int FindNewline(byte[] data, int pos)
    {
        int p = pos;
        while (p < data.Length && data[p] != (byte)'\n') p++;
        return p;
    }
}
