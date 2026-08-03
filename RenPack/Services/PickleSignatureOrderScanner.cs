using System.Buffers.Binary;
using System.Text;

namespace RenPack.Services;

/// <summary>
/// Ergebnis von <see cref="PickleSignatureOrderScanner.Scan"/> — Queues in
/// pickle-DFS-Order fuer die verschiedenen Dict-Felder, die von der
/// Hashtable-Order-Umsortierung betroffen sind.
/// </summary>
public sealed class PickleDictOrderResult
{
    /// <summary><c>renpy.parameter.Signature.parameters</c> — Label/Screen-Params.</summary>
    public Queue<List<string>> SignatureParameters { get; } = new();

    /// <summary><c>renpy.ast.Style.properties</c> — Style-Properties.</summary>
    public Queue<List<string>> StyleProperties { get; } = new();
}

/// <summary>
/// Mini-Pickle-VM zum Extrahieren der Insertion-Order von Ren'Py-Dicts, die
/// von der <see cref="System.Collections.Hashtable"/>-Bucket-Order-Falle
/// betroffen sind:
///
/// <list type="bullet">
///   <item><c>renpy.parameter.Signature.parameters</c> — kritisch, weil
///     label/screen-Params per Position gebunden werden.</item>
///   <item><c>renpy.ast.Style.properties</c> — kosmetisch, aber wichtig
///     fuer Diff-freundliche Decompile-Ausgabe.</item>
/// </list>
///
/// Razorvine.Pickle nutzt intern Hashtable und verliert die Insertion-Order.
/// Wir scannen die Pickle-Bytes ein zweites Mal mit einer minimalen eigenen
/// VM und emittieren die Reihenfolge pro Instanz in pickle-DFS-Order.
/// </summary>
public static class PickleSignatureOrderScanner
{
    // Kombinationen (Module, Klasse, StateField) die wir tracken. Alle
    // haben denselben Zugriffs-Pfad: BUILD auf Instanz, state ist Tuple
    // (slots, dict), dict[field] ist der Sub-Dict dessen Reihenfolge wir
    // brauchen.
    private const string SigModule = "renpy.parameter";
    private const string SigClass = "Signature";
    private const string SigField = "parameters";
    private const string StyleModule = "renpy.ast";
    private const string StyleClass = "Style";
    private const string StyleField = "properties";

    private static readonly object MarkSentinel = new();

    private sealed record ClassRef(string Module, string Name);

    private sealed class Instance
    {
        public required ClassRef Class { get; init; }
    }

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

    private sealed class TupleMarker
    {
        public required object?[] Items { get; init; }
    }

    /// <summary>Convenience-Overload — liefert nur die Signature-Params-Queue
    /// (Backward-Compat fuer Aufrufer die nichts weiter brauchen).</summary>
    public static Queue<List<string>> Scan(byte[] pickle) => ScanAll(pickle).SignatureParameters;

    /// <summary>Voller Scan — liefert alle getrackten Dict-Reihenfolgen.</summary>
    public static PickleDictOrderResult ScanAll(byte[] pickle)
    {
        var stack = new List<object?>(64);
        var memo = new Dictionary<int, object?>(256);
        int nextMemoAuto = 0;
        var result = new PickleDictOrderResult();
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

                case 0x4E: // NONE
                    stack.Add(null); break;
                case 0x88: // NEWTRUE
                    stack.Add(true); break;
                case 0x89: // NEWFALSE
                    stack.Add(false); break;

                case 0x49: // INT (text)
                    stack.Add(null); pos = SkipUntilNewline(pickle, pos); break;
                case 0x4A: // BININT
                    stack.Add(null); pos += 4; break;
                case 0x4B: // BININT1
                    stack.Add(null); pos += 1; break;
                case 0x4D: // BININT2
                    stack.Add(null); pos += 2; break;
                case 0x4C: // LONG
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

                case 0x46: // FLOAT (text)
                    stack.Add(null); pos = SkipUntilNewline(pickle, pos); break;
                case 0x47: // BINFLOAT (8-byte big-endian)
                    stack.Add(null); pos += 8; break;

                case 0x53: // STRING (text)
                    stack.Add(null); pos = SkipUntilNewline(pickle, pos); break;
                case 0x54: // BINSTRING (4-byte)
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

                case 0x42: // BINBYTES
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
                case 0x8E: // BINBYTES8 (8-byte)
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

                case 0x56: // UNICODE (text)
                    stack.Add(null); pos = SkipUntilNewline(pickle, pos); break;
                case 0x58: // BINUNICODE (4-byte)
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
                case 0x8D: // BINUNICODE8 (8-byte)
                    {
                        long len = BinaryPrimitives.ReadInt64LittleEndian(pickle.AsSpan(pos, 8));
                        pos += 8;
                        stack.Add(Encoding.UTF8.GetString(pickle, pos, (int)len));
                        pos += (int)len;
                    }
                    break;

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
                case 0x6C: // LIST
                    PopToMark(stack); stack.Add(null); break;
                case 0x64: // DICT (mark-based)
                    {
                        var items = PopToMark(stack);
                        var d = new OrderedDictMarker();
                        for (int i = 0; i + 1 < items.Length; i += 2)
                            d.Entries.Add(new KeyValuePair<object?, object?>(items[i], items[i + 1]));
                        stack.Add(d);
                    }
                    break;

                case 0x61: // APPEND
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    break;
                case 0x65: // APPENDS
                    PopToMark(stack);
                    break;
                case 0x90: // ADDITEMS
                    PopToMark(stack);
                    break;

                case 0x73: // SETITEM
                    {
                        var v = stack[^1]; stack.RemoveAt(stack.Count - 1);
                        var k = stack[^1]; stack.RemoveAt(stack.Count - 1);
                        if (stack[^1] is OrderedDictMarker d)
                            d.Entries.Add(new KeyValuePair<object?, object?>(k, v));
                    }
                    break;
                case 0x75: // SETITEMS
                    {
                        var items = PopToMark(stack);
                        if (stack[^1] is OrderedDictMarker d)
                        {
                            for (int i = 0; i + 1 < items.Length; i += 2)
                                d.Entries.Add(new KeyValuePair<object?, object?>(items[i], items[i + 1]));
                        }
                    }
                    break;

                case 0x67: // GET (text)
                    {
                        int end = FindNewline(pickle, pos);
                        int idx = int.Parse(Encoding.ASCII.GetString(pickle, pos, end - pos));
                        pos = end + 1;
                        stack.Add(memo.TryGetValue(idx, out var v) ? v : null);
                    }
                    break;
                case 0x68: // BINGET
                    {
                        int idx = pickle[pos++];
                        stack.Add(memo.TryGetValue(idx, out var v) ? v : null);
                    }
                    break;
                case 0x6A: // LONG_BINGET
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

                case 0x63: // GLOBAL
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
                case 0x93: // STACK_GLOBAL
                    {
                        var name = stack[^1] as string; stack.RemoveAt(stack.Count - 1);
                        var mod = stack[^1] as string; stack.RemoveAt(stack.Count - 1);
                        stack.Add(new ClassRef(mod ?? "", name ?? ""));
                    }
                    break;
                case 0x69: // INST
                    {
                        int end1 = FindNewline(pickle, pos);
                        string mod = Encoding.UTF8.GetString(pickle, pos, end1 - pos);
                        pos = end1 + 1;
                        int end2 = FindNewline(pickle, pos);
                        string name = Encoding.UTF8.GetString(pickle, pos, end2 - pos);
                        pos = end2 + 1;
                        PopToMark(stack);
                        stack.Add(new Instance { Class = new ClassRef(mod, name) });
                    }
                    break;
                case 0x6F: // OBJ
                    {
                        var items = PopToMark(stack);
                        var cls = items.Length > 0 ? items[0] as ClassRef : null;
                        stack.Add(cls is not null
                            ? new Instance { Class = cls }
                            : (object?)null);
                    }
                    break;
                case 0x52: // REDUCE
                    {
                        stack.RemoveAt(stack.Count - 1);
                        var cls = stack[^1] as ClassRef;
                        stack.RemoveAt(stack.Count - 1);
                        stack.Add(cls is not null
                            ? new Instance { Class = cls }
                            : (object?)null);
                    }
                    break;
                case 0x81: // NEWOBJ
                    {
                        stack.RemoveAt(stack.Count - 1);
                        var cls = stack[^1] as ClassRef;
                        stack.RemoveAt(stack.Count - 1);
                        stack.Add(cls is not null
                            ? new Instance { Class = cls }
                            : (object?)null);
                    }
                    break;
                case 0x92: // NEWOBJ_EX
                    {
                        stack.RemoveAt(stack.Count - 1);
                        stack.RemoveAt(stack.Count - 1);
                        var cls = stack[^1] as ClassRef;
                        stack.RemoveAt(stack.Count - 1);
                        stack.Add(cls is not null
                            ? new Instance { Class = cls }
                            : (object?)null);
                    }
                    break;
                case 0x62: // BUILD
                    {
                        var state = stack[^1]; stack.RemoveAt(stack.Count - 1);
                        var obj = stack[^1];
                        if (obj is Instance inst)
                        {
                            OrderedDictMarker? stateDict = state switch
                            {
                                TupleMarker t when t.Items.Length >= 2 => t.Items[1] as OrderedDictMarker,
                                OrderedDictMarker d => d,
                                _ => null,
                            };
                            if (stateDict is not null)
                            {
                                if (inst.Class.Module == SigModule && inst.Class.Name == SigClass
                                    && stateDict.Get(SigField) is OrderedDictMarker sigDict)
                                {
                                    result.SignatureParameters.Enqueue(sigDict.StringKeys());
                                }
                                else if (inst.Class.Module == StyleModule && inst.Class.Name == StyleClass
                                    && stateDict.Get(StyleField) is OrderedDictMarker styleDict)
                                {
                                    result.StyleProperties.Enqueue(styleDict.StringKeys());
                                }
                            }
                        }
                    }
                    break;

                case 0x50: // PERSID
                    stack.Add(null); pos = SkipUntilNewline(pickle, pos); break;
                case 0x51: // BINPERSID
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    stack.Add(null);
                    break;

                case 0x82: pos += 1; stack.Add(null); break;
                case 0x83: pos += 2; stack.Add(null); break;
                case 0x84: pos += 4; stack.Add(null); break;

                case 0x80: pos++; break;
                case 0x95: pos += 8; break;

                case 0x97: stack.Add(null); break;
                case 0x98: break;

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
