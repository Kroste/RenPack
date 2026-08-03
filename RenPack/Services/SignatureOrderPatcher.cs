using System.Collections;
using Razorvine.Pickle.Objects;

namespace RenPack.Services;

/// <summary>
/// Walkt die dekompilierte AST rekursiv und ersetzt die Dict-Felder, die von
/// der <see cref="System.Collections.Hashtable"/>-Bucket-Reihenfolge betroffen
/// sind, durch order-preserving <see cref="Dictionary{TKey,TValue}"/>s. Die
/// korrekte Reihenfolge kommt aus <see cref="PickleSignatureOrderScanner"/>.
///
/// Getrackte Felder:
/// <list type="bullet">
///   <item><c>renpy.parameter.Signature.parameters</c> — label/screen-Params
///     (Positional-Bindung waere sonst kaputt).</item>
///   <item><c>renpy.ast.Style.properties</c> — kosmetisch, aber wichtig fuer
///     Diff-freundliche Ausgabe.</item>
/// </list>
///
/// Da Pickle-DFS-Order == AST-DFS-Order (Razorvine deserialisiert in
/// pickle-Reihenfolge, wir walken die AST in derselben Ordnung), koennen wir
/// die Queues positional pop-en.
/// </summary>
public static class SignatureOrderPatcher
{
    private const string SignatureClassName = "renpy.parameter.Signature";
    private const string SignatureField = "parameters";
    private const string StyleClassName = "renpy.ast.Style";
    private const string StyleField = "properties";

    /// <summary>Backward-Compat-Overload — nutzt die Queue nur fuer
    /// Signature-Params.</summary>
    public static void PatchInPlace(IEnumerable<object?> statements, Queue<List<string>> signatureQueue)
    {
        var result = new PickleDictOrderResult();
        while (signatureQueue.TryDequeue(out var list))
            result.SignatureParameters.Enqueue(list);
        PatchInPlace(statements, result);
    }

    /// <summary>Voller Patcher — kriegt beide Queues und ersetzt Hashtables
    /// in AST-DFS-Order durch Dictionarys in korrekter Reihenfolge.</summary>
    public static void PatchInPlace(IEnumerable<object?> statements, PickleDictOrderResult orderings)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var s in statements) Walk(s, orderings, seen);
    }

    private static void Walk(object? o, PickleDictOrderResult orderings, HashSet<object> seen)
    {
        if (o is null) return;
        if (o is string) return;
        if (!seen.Add(o)) return;

        if (o is ClassDict cd)
        {
            if (cd.ClassName == SignatureClassName)
                TryReorder(cd, SignatureField, orderings.SignatureParameters);
            else if (cd.ClassName == StyleClassName)
                TryReorder(cd, StyleField, orderings.StyleProperties);

            foreach (var v in cd.Values) Walk(v, orderings, seen);
        }
        else if (o is IList list)
        {
            foreach (var v in list) Walk(v, orderings, seen);
        }
        else if (o is IDictionary dict)
        {
            foreach (DictionaryEntry e in dict)
            {
                Walk(e.Key, orderings, seen);
                Walk(e.Value, orderings, seen);
            }
        }
    }

    private static void TryReorder(ClassDict cd, string field, Queue<List<string>> queue)
    {
        if (!cd.TryGetValue(field, out var raw) || raw is not Hashtable ht) return;
        if (!queue.TryDequeue(out var names)) return;

        var ordered = new Dictionary<string, object?>(names.Count);
        foreach (var name in names)
        {
            if (ht.ContainsKey(name))
                ordered[name] = ht[name];
        }
        // Fallback: falls die Hashtable Keys enthaelt, die nicht in der
        // Order-List sind (sollte nicht vorkommen), hinten anhaengen.
        foreach (DictionaryEntry e in ht)
        {
            if (e.Key is string sk && !ordered.ContainsKey(sk))
                ordered[sk] = e.Value;
        }
        cd[field] = ordered;
    }
}
