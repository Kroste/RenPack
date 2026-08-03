using System.Collections;
using Razorvine.Pickle.Objects;

namespace RenPack.Services;

/// <summary>
/// Walkt die dekompilierte AST rekursiv, findet <c>renpy.parameter.Signature</c>-
/// Instanzen und ersetzt deren <c>parameters</c>-<see cref="Hashtable"/> durch
/// eine ordered <see cref="Dictionary{TKey,TValue}"/> in der korrekten
/// Insertion-Reihenfolge — geliefert von <see cref="PickleSignatureOrderScanner"/>.
///
/// Warum noetig: Razorvine.Pickle deserialisiert Python-Dicts als
/// <see cref="Hashtable"/>, dessen Enumerator in Hash-Bucket-Reihenfolge laeuft
/// (nicht in Insertion-Order). Fuer normale Store-Vars ist das egal, aber
/// bei Label/Screen-Parametern zerstoert das die Positional-Bindung —
/// <c>label X(a, b)</c> wird zu <c>label X(b, a)</c>.
///
/// Der Scanner liefert die richtige Reihenfolge als Queue in pickle-DFS-Order.
/// Wir walken die AST in derselben Reihenfolge und dequeueen pro Signature.
/// </summary>
public static class SignatureOrderPatcher
{
    private const string SignatureClassName = "renpy.parameter.Signature";
    private const string ParametersField = "parameters";

    /// <summary>Patcht alle Signature-Objekte in-place. Nach dem Aufruf
    /// enthaelt jede Signature.parameters ein Dictionary in korrekter
    /// Reihenfolge statt einer Hashtable.</summary>
    public static void PatchInPlace(IEnumerable<object?> statements, Queue<List<string>> orderQueue)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var s in statements) Walk(s, orderQueue, seen);
    }

    private static void Walk(object? o, Queue<List<string>> queue, HashSet<object> seen)
    {
        if (o is null) return;
        if (o is string) return;
        if (!seen.Add(o)) return;

        if (o is ClassDict cd)
        {
            if (cd.ClassName == SignatureClassName
                && cd.TryGetValue(ParametersField, out var raw)
                && raw is Hashtable ht
                && queue.TryDequeue(out var names))
            {
                var ordered = new Dictionary<string, object?>(names.Count);
                foreach (var name in names)
                {
                    if (ht.ContainsKey(name))
                        ordered[name] = ht[name];
                }
                // Falls die Hashtable Keys enthaelt, die nicht in der Order-List
                // sind (sollte nicht vorkommen, aber safe): hinten anhaengen.
                foreach (DictionaryEntry e in ht)
                {
                    if (e.Key is string sk && !ordered.ContainsKey(sk))
                        ordered[sk] = e.Value;
                }
                cd[ParametersField] = ordered;
            }
            // Trotzdem weiter descenden — Signature koennen in tieferen Nodes
            // stecken (Screen mit sub-Screens etc.).
            foreach (var v in cd.Values) Walk(v, queue, seen);
        }
        else if (o is IList list)
        {
            foreach (var v in list) Walk(v, queue, seen);
        }
        else if (o is IDictionary dict)
        {
            foreach (DictionaryEntry e in dict)
            {
                Walk(e.Key, queue, seen);
                Walk(e.Value, queue, seen);
            }
        }
    }
}
