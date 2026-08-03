using System.Collections;
using FluentAssertions;
using RenPack.Services;
using Razorvine.Pickle.Objects;
using Xunit;

namespace RenPack.Tests;

public sealed class PickleSignatureOrderScannerTests
{
    /// <summary>
    /// Baut ein minimales Pickle von Hand, das eine Signature-Instanz mit
    /// drei Parametern in einer bestimmten Reihenfolge enthaelt. Der Scanner
    /// muss diese Reihenfolge exakt so zurueckgeben.
    /// </summary>
    private static byte[] BuildSignaturePickle(params string[] paramNames)
    {
        var bytes = new List<byte>
        {
            0x80, 0x05,                                // PROTO 5
            0x95, 0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,  // FRAME (dummy len)
        };
        void PushShortUnicode(string s)
        {
            bytes.Add(0x8C);
            var utf = System.Text.Encoding.UTF8.GetBytes(s);
            bytes.Add((byte)utf.Length);
            bytes.AddRange(utf);
        }
        PushShortUnicode("renpy.parameter");
        bytes.Add(0x94);                               // MEMOIZE
        PushShortUnicode("Signature");
        bytes.Add(0x94);
        bytes.Add(0x93);                               // STACK_GLOBAL
        bytes.Add(0x29);                               // EMPTY_TUPLE
        bytes.Add(0x81);                               // NEWOBJ (Signature instance)
        bytes.Add(0x4E);                               // NONE (state[0])
        bytes.Add(0x7D);                               // EMPTY_DICT (state[1])
        bytes.Add(0x28);                               // MARK
        PushShortUnicode("parameters");                // key
        bytes.Add(0x7D);                               // EMPTY_DICT (parameters sub-dict)
        bytes.Add(0x28);                               // MARK
        foreach (var pname in paramNames)
        {
            PushShortUnicode(pname);
            bytes.Add(0x4E); // Parameter value stub (we use None)
        }
        bytes.Add(0x75);                               // SETITEMS (fills parameters dict)
        bytes.Add(0x75);                               // SETITEMS (fills state dict)
        bytes.Add(0x86);                               // TUPLE2
        bytes.Add(0x62);                               // BUILD (applies state)
        bytes.Add(0x2E);                               // STOP
        return bytes.ToArray();
    }

    [Fact]
    public void Extracts_parameter_names_in_pickle_insertion_order()
    {
        var pickle = BuildSignaturePickle("foo", "bar", "baz");
        var queue = PickleSignatureOrderScanner.Scan(pickle);
        queue.Should().HaveCount(1);
        queue.Peek().Should().Equal("foo", "bar", "baz");
    }

    [Fact]
    public void Emits_multiple_signatures_in_pickle_order()
    {
        // Zwei Signature-Instanzen hintereinander konkateniert (nicht ganz
        // Pickle-korrekt weil zwei STOPs, aber wir stoppen beim ersten):
        // stattdessen zwei Signaturen im gleichen Stream ueber MARKers.
        var bytes = new List<byte> { 0x80, 0x05 };

        // Wir bauen ein Pickle mit einer Liste die zwei Signature-Objekte
        // enthaelt.
        static byte[] Sig(string[] pnames)
        {
            var b = new List<byte>();
            void PushShortUnicode(string s)
            {
                b.Add(0x8C);
                var utf = System.Text.Encoding.UTF8.GetBytes(s);
                b.Add((byte)utf.Length);
                b.AddRange(utf);
            }
            PushShortUnicode("renpy.parameter");
            b.Add(0x94);
            PushShortUnicode("Signature");
            b.Add(0x94);
            b.Add(0x93);      // STACK_GLOBAL
            b.Add(0x29);      // EMPTY_TUPLE
            b.Add(0x81);      // NEWOBJ
            b.Add(0x4E);      // NONE
            b.Add(0x7D);      // EMPTY_DICT (state)
            b.Add(0x28);      // MARK
            PushShortUnicode("parameters");
            b.Add(0x7D);      // EMPTY_DICT (params)
            b.Add(0x28);      // MARK
            foreach (var p in pnames) { PushShortUnicode(p); b.Add(0x4E); }
            b.Add(0x75);      // SETITEMS (params)
            b.Add(0x75);      // SETITEMS (state)
            b.Add(0x86);      // TUPLE2
            b.Add(0x62);      // BUILD
            return b.ToArray();
        }

        bytes.Add(0x95); bytes.AddRange(new byte[8]);       // FRAME
        bytes.Add(0x5D);                                     // EMPTY_LIST
        bytes.Add(0x28);                                     // MARK
        bytes.AddRange(Sig(new[] { "who", "what" }));
        bytes.AddRange(Sig(new[] { "unlocked_list", "index" }));
        bytes.Add(0x65);                                     // APPENDS
        bytes.Add(0x2E);                                     // STOP

        var q = PickleSignatureOrderScanner.Scan(bytes.ToArray());
        q.Should().HaveCount(2);
        q.Dequeue().Should().Equal("who", "what");
        q.Dequeue().Should().Equal("unlocked_list", "index");
    }

    [Fact]
    public void Ignores_non_signature_classes()
    {
        // Baue Pickle mit einer NICHT-Signature-Klasse mit einem
        // parameters-Feld. Scanner darf nichts liefern.
        var bytes = new List<byte>
        {
            0x80, 0x05,
            0x95, 0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        };
        void PushShortUnicode(string s)
        {
            bytes.Add(0x8C);
            var utf = System.Text.Encoding.UTF8.GetBytes(s);
            bytes.Add((byte)utf.Length);
            bytes.AddRange(utf);
        }
        PushShortUnicode("some.module");
        PushShortUnicode("OtherClass");
        bytes.Add(0x93);
        bytes.Add(0x29);
        bytes.Add(0x81);
        bytes.Add(0x4E);
        bytes.Add(0x7D);
        bytes.Add(0x28);
        PushShortUnicode("parameters");
        bytes.Add(0x7D);
        bytes.Add(0x28);
        PushShortUnicode("x");
        bytes.Add(0x4E);
        bytes.Add(0x75);
        bytes.Add(0x75);
        bytes.Add(0x86);
        bytes.Add(0x62);
        bytes.Add(0x2E);

        var q = PickleSignatureOrderScanner.Scan(bytes.ToArray());
        q.Should().BeEmpty();
    }

    [Fact]
    public void Patcher_replaces_signature_parameters_hashtable()
    {
        // Simuliert das End-to-End-Setup: eine "Label"-ClassDict mit einer
        // "Signature"-ClassDict deren parameters-Hashtable falsche
        // Reihenfolge hat. Patcher muss den Hashtable durch ein Dictionary
        // in der Queue-Order ersetzen.
        var sig = new ClassDict("renpy.parameter", "Signature");
        var ht = new Hashtable
        {
            ["b"] = "bValue",
            ["a"] = "aValue",
        };
        sig["parameters"] = ht;

        var label = new ClassDict("renpy.ast", "Label");
        label["parameters"] = sig;
        label["_name"] = "test";

        var stmts = new object?[] { label };
        var queue = new Queue<List<string>>();
        queue.Enqueue(new List<string> { "a", "b" });
        SignatureOrderPatcher.PatchInPlace(stmts, queue);

        sig["parameters"].Should().BeAssignableTo<Dictionary<string, object?>>();
        var patched = (Dictionary<string, object?>)sig["parameters"]!;
        patched.Keys.Should().Equal("a", "b");
        patched["a"].Should().Be("aValue");
        patched["b"].Should().Be("bValue");
    }
}
