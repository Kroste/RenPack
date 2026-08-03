using System.Collections;
using FluentAssertions;
using Razorvine.Pickle;
using Razorvine.Pickle.Objects;
using RenPack.Services;
using Xunit;

namespace RenPack.Tests;

/// <summary>
/// Robustness-Tests fuer den Catch-all-Unpickler: unbekannte Klassen mit
/// dict-artigen Namen (endet auf "Dict"/"dict", oder "Counter") muessen als
/// Hashtable-basierte Objekte konstruiert werden — sonst crasht Razorvine's
/// <c>load_setitem</c> beim expliziten Cast auf <see cref="Hashtable"/>.
///
/// Praeventiv fuer Ren'Py 8.6+ und aehnliche Zukunftsversionen mit neuen
/// dict-Subklassen.
/// </summary>
public sealed class RenpySaveServiceCatchAllTests
{
    static RenpySaveServiceCatchAllTests()
    {
        // Catch-all einmalig aktivieren (idempotent — RenpySaveService-Ctor
        // patcht die Object-Constructors nur beim ersten Aufruf).
        _ = new RenpySaveService();
    }

    /// <summary>Baut ein Pickle mit einer unbekannten dict-artigen Klasse
    /// (`some.module.FancyDict`) das SETITEM verwendet — genau das Muster
    /// das ohne die Heuristik crasht.</summary>
    private static byte[] BuildFancyDictPickle(string className)
    {
        var b = new List<byte>();
        void PushShortUnicode(string s)
        {
            b.Add(0x8C);
            var utf = System.Text.Encoding.UTF8.GetBytes(s);
            b.Add((byte)utf.Length);
            b.AddRange(utf);
        }
        b.Add(0x80); b.Add(0x05); // PROTO 5
        b.Add(0x95); b.AddRange(new byte[8]); // FRAME
        PushShortUnicode("some.module");
        PushShortUnicode(className);
        b.Add(0x93); // STACK_GLOBAL
        b.Add(0x29); // EMPTY_TUPLE
        b.Add(0x52); // REDUCE — konstruiert Instanz per registriertem Ctor
        // SETITEM: pushen (key, value) und SETITEM
        PushShortUnicode("k1");
        b.Add(0x4B); b.Add(0x11); // BININT1 17
        b.Add(0x73); // SETITEM
        b.Add(0x2E); // STOP
        return b.ToArray();
    }

    [Fact]
    public void Unknown_class_ending_in_Dict_does_not_crash_on_setitem()
    {
        var pickle = BuildFancyDictPickle("FancyDict");
        using var u = new Unpickler();
        var result = u.loads(pickle);
        // Ergebnis: Hashtable mit dem Key. Der Klassenname geht dabei
        // verloren — das ist der akzeptierte Trade-off (Robustness > Fidelity).
        result.Should().BeAssignableTo<IDictionary>();
        var d = (IDictionary)result!;
        d["k1"].Should().Be(17);
    }

    [Fact]
    public void Unknown_class_named_Counter_does_not_crash()
    {
        var pickle = BuildFancyDictPickle("Counter");
        using var u = new Unpickler();
        var result = u.loads(pickle);
        result.Should().BeAssignableTo<IDictionary>();
    }

    [Fact]
    public void OrderedDict_with_setitem_does_not_crash()
    {
        // Regression: `collections.OrderedDict` mit SETITEM (single-item-Muster)
        // crashte frueher, weil OrderedDictContainer Dictionary<K,V> extendete
        // statt Hashtable — Razorvine's load_setitem macht expliziten
        // (Hashtable)-Cast. Fix: OrderedDictContainer erbt jetzt von Hashtable.
        // Insertion-Order geht dabei verloren — Ren'Py-relevante OrderedDict-
        // Faelle (Signature.parameters, Style.properties) haben eigenes
        // Order-Tracking ueber den PickleSignatureOrderScanner.
        var b = new List<byte>();
        void PushShortUnicode(string s)
        {
            b.Add(0x8C);
            var utf = System.Text.Encoding.UTF8.GetBytes(s);
            b.Add((byte)utf.Length);
            b.AddRange(utf);
        }
        b.Add(0x80); b.Add(0x05);
        b.Add(0x95); b.AddRange(new byte[8]);
        PushShortUnicode("collections");
        PushShortUnicode("OrderedDict");
        b.Add(0x93); // STACK_GLOBAL
        b.Add(0x29); // EMPTY_TUPLE
        b.Add(0x52); // REDUCE
        PushShortUnicode("k1");
        b.Add(0x4B); b.Add(0x2A); // BININT1 42
        b.Add(0x73); // SETITEM
        b.Add(0x2E); // STOP

        using var u = new Unpickler();
        var result = u.loads(b.ToArray());
        result.Should().BeAssignableTo<IDictionary>();
        var d = (IDictionary)result!;
        d["k1"].Should().Be(42);
    }

    [Fact]
    public void Unknown_non_dict_class_still_gets_opaque_ClassDict()
    {
        // Namen ohne "Dict"/"Counter"-Muster: OpaqueCtor → RenpyOpaqueDict.
        // Wir bauen ein Pickle das NUR NEWOBJ + BUILD verwendet (kein SETITEM,
        // sonst crasht's — genau der Fall den die Heuristik NICHT abfaengt).
        var b = new List<byte>();
        void PushShortUnicode(string s)
        {
            b.Add(0x8C);
            var utf = System.Text.Encoding.UTF8.GetBytes(s);
            b.Add((byte)utf.Length);
            b.AddRange(utf);
        }
        b.Add(0x80); b.Add(0x05);
        b.Add(0x95); b.AddRange(new byte[8]);
        PushShortUnicode("some.module");
        PushShortUnicode("MyClass");
        b.Add(0x93); // STACK_GLOBAL
        b.Add(0x29); // EMPTY_TUPLE
        b.Add(0x81); // NEWOBJ
        b.Add(0x4E); // NONE (state[0])
        b.Add(0x7D); // EMPTY_DICT (state[1])
        b.Add(0x86); // TUPLE2
        b.Add(0x62); // BUILD
        b.Add(0x2E); // STOP

        using var u = new Unpickler();
        var result = u.loads(b.ToArray());
        // Should be a ClassDict (RenpyOpaqueDict extends ClassDict).
        result.Should().BeAssignableTo<ClassDict>();
    }
}
