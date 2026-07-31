using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLog;
using Razorvine.Pickle;
using Razorvine.Pickle.Objects;

namespace RenPack.Services;

/// <summary>
/// Liest Ren'Py-Saves (.save). Format-Referenz: renpy.savelocation und
/// renpy.loadsave im Ren'Py-Quellcode.
///
/// Aufbau eines Saves:
///   ZIP-Container mit Einträgen:
///     - "log"         zlib-komprimiertes Pickle (protocol 2) von (roots, log)
///                     — roots = Dict aller Store-Namespaces, log = RollbackLog
///     - "json"        JSON-Metadaten (save_name, save_time, renpy_version …)
///     - "screenshot.png"  PNG-Vorschau
///     - "signatures"  (optional) HMAC-Signaturen
/// </summary>
public sealed partial class RenpySaveService : IRenpySaveService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private static int _constructorsRegistered;

    public RenpySaveService() => EnsureConstructorsRegistered();

    public SaveInfo Read(string savePath)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Info("Lese Save: {path}", savePath);

        using var zip = ZipFile.OpenRead(savePath);

        var metadata = ReadMetadata(zip);
        byte[]? screenshot = ReadEntryBytes(zip, "screenshot.png");
        string? logError = null;
        IReadOnlyList<SaveVariable> variables = [];

        try
        {
            object? logRoot = ReadLog(zip);
            variables = ExtractVariables(logRoot);
            Log.Info("Save gelesen: {vars} Variable(n), {ms} ms",
                variables.Count, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            logError = ex.Message;
            Log.Warn(ex, "Log-Eintrag im Save konnte nicht dekodiert werden: {path}", savePath);
        }

        return new SaveInfo(savePath, metadata, screenshot, variables, logError);
    }

    // ---- Metadaten ---------------------------------------------------------

    private static SaveMetadata ReadMetadata(ZipArchive zip)
    {
        byte[]? jsonBytes = ReadEntryBytes(zip, "json");
        if (jsonBytes is null || jsonBytes.Length == 0)
            return new SaveMetadata(null, null, null, null,
                new Dictionary<string, object?>());

        try
        {
            using var doc = JsonDocument.Parse(jsonBytes);
            var raw = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
                raw[p.Name] = JsonToObject(p.Value);

            return new SaveMetadata(
                SaveName: raw.TryGetValue("_save_name", out var n) ? n?.ToString() : null,
                SaveTime: TryReadUnix(raw, "_save_time"),
                RenpyVersion: raw.TryGetValue("_renpy_version", out var v) ? v?.ToString() : null,
                GameName: raw.TryGetValue("_game_name", out var g) ? g?.ToString() : null,
                Raw: raw);
        }
        catch (JsonException ex)
        {
            Log.Warn(ex, "Save-JSON-Metadaten unlesbar");
            return new SaveMetadata(null, null, null, null,
                new Dictionary<string, object?>());
        }
    }

    private static DateTimeOffset? TryReadUnix(IDictionary<string, object?> raw, string key)
    {
        if (!raw.TryGetValue(key, out var val) || val is null) return null;
        try
        {
            double seconds = Convert.ToDouble(val, CultureInfo.InvariantCulture);
            return DateTimeOffset.FromUnixTimeSeconds((long)seconds).ToLocalTime();
        }
        catch { return null; }
    }

    private static object? JsonToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var i) ? i : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => el.EnumerateArray().Select(JsonToObject).ToList(),
        JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => JsonToObject(p.Value)),
        _ => el.ToString(),
    };

    // ---- Log entpicklen ----------------------------------------------------

    private static object? ReadLog(ZipArchive zip)
    {
        byte[] logBytes = ReadEntryBytes(zip, "log")
            ?? throw new InvalidDataException("Save enthält keinen 'log'-Eintrag.");

        // Ren'Py komprimiert den Log meist mit zlib; ältere Saves haben rohes
        // Pickle. Diskriminator: zlib beginnt mit 0x78, Pickle-Proto-2 mit 0x80.
        byte[] pickle = logBytes.Length > 0 && logBytes[0] == 0x78
            ? ZlibDecompress(logBytes) : logBytes;

        return LoadWithFallback(pickle);
    }

    /// <summary>Unpickle mit iterativem Fallback: unbekannte Klassen werden bei
    /// Bedarf als Passthrough registriert und erneut versucht. Verhindert, dass
    /// eine einzelne unbekannte Ren'Py-Klasse den ganzen Save-Reader lahmlegt.</summary>
    private static object? LoadWithFallback(byte[] pickle)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var u = new Unpickler();
                return u.loads(pickle);
            }
            catch (PickleException ex)
            {
                // Fehlermeldung: "expected zero arguments for construction of ClassDict (for mod.Class)."
                var m = UnknownClassPattern().Match(ex.Message);
                if (!m.Success) throw;

                string module = m.Groups["mod"].Value;
                string name = m.Groups["cls"].Value;
                Log.Debug("Registriere Passthrough für unbekannte Klasse {mod}.{cls}", module, name);
                Unpickler.registerConstructor(module, name, new PassthroughConstructor(module, name));
            }
        }
        throw new PickleException("Unpickle abgebrochen: zu viele unbekannte Klassen (>50).");
    }

    [GeneratedRegex(@"for construction of ClassDict \(for (?<mod>[^.]+)\.(?<cls>[^)]+)\)")]
    private static partial Regex UnknownClassPattern();

    // ---- Store-Extraktion --------------------------------------------------

    /// <summary>Sucht im entpickleten Log das <c>store</c>-Dict des zuletzt
    /// gespeicherten Zustands und dreht seine Einträge in Anzeige-Variablen um.
    /// Struktur-Referenz Ren'Py 8.x: Top-Level ist ein Tupel <c>(roots, log)</c>,
    /// <c>roots</c> ist ein Dict mit Store-Namespaces (Key <c>"store"</c> ist der
    /// Haupt-Store). Fallback: rekursive Suche nach dem größten flachen Dict.</summary>
    private static IReadOnlyList<SaveVariable> ExtractVariables(object? logRoot)
    {
        IDictionary? store = FindStore(logRoot);
        if (store is null) return [];

        var result = new List<SaveVariable>(store.Count);
        foreach (DictionaryEntry de in store)
        {
            string key = de.Key?.ToString() ?? "";
            result.Add(new SaveVariable(
                Name: key,
                TypeName: TypeDisplayName(de.Value),
                Value: ValueDisplay(de.Value),
                IsInternal: key.StartsWith('_')));
        }
        result.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    private static IDictionary? FindStore(object? root)
    {
        // Ren'Py 8.x Top-Level: (roots, log). roots ist ein Dict, "store" ist der Schlüssel.
        if (root is object?[] { Length: >= 1 } arr && arr[0] is IDictionary roots)
        {
            if (roots.Contains("store") && roots["store"] is IDictionary rootsStore)
                return rootsStore;
            // Ältere Ren'Py-Versionen: roots IST direkt der Store.
            if (LooksLikeStore(roots)) return roots;
        }

        // Fallback: rekursiv nach dem plausibelsten Dict suchen.
        return FindLargestStoreLike(root, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    private static IDictionary? FindLargestStoreLike(object? obj, HashSet<object> seen)
    {
        if (obj is null || !seen.Add(obj)) return null;

        IDictionary? best = obj is IDictionary d && LooksLikeStore(d) ? d : null;

        switch (obj)
        {
            case IDictionary dict:
                foreach (var v in dict.Values)
                {
                    var found = FindLargestStoreLike(v, seen);
                    if (found is not null && (best is null || found.Count > best.Count)) best = found;
                }
                break;
            case IEnumerable list when obj is not string:
                foreach (var v in list)
                {
                    var found = FindLargestStoreLike(v, seen);
                    if (found is not null && (best is null || found.Count > best.Count)) best = found;
                }
                break;
        }
        return best;
    }

    /// <summary>Heuristik: Ein Store-Dict hat String-Keys und enthält typische
    /// Ren'Py-Kennungen (<c>_menu</c>, <c>_return</c>, <c>_args</c>, …).</summary>
    private static bool LooksLikeStore(IDictionary dict)
    {
        if (dict.Count < 3) return false;
        int stringKeys = 0, renpyMarkers = 0;
        foreach (var k in dict.Keys)
        {
            if (k is not string s) continue;
            stringKeys++;
            if (s is "_menu" or "_return" or "_args" or "_kwargs" or "_scope"
                or "_history_list" or "nvl_list" or "_last_say_who") renpyMarkers++;
        }
        return stringKeys == dict.Count && renpyMarkers >= 1;
    }

    private static string TypeDisplayName(object? v) => v switch
    {
        null => "None",
        string => "str",
        bool => "bool",
        int or long or short or byte or sbyte or uint or ulong or ushort => "int",
        double or float or decimal => "float",
        ClassDict cd => ShortName(cd.ClassName),
        IDictionary => "dict",
        object?[] => "tuple",
        ArrayList => "list",
        IList => "list",
        _ => ShortName(v.GetType().Name),
    };

    private static string ShortName(string qualified)
    {
        int dot = qualified.LastIndexOf('.');
        return dot >= 0 ? qualified[(dot + 1)..] : qualified;
    }

    private static string ValueDisplay(object? v)
    {
        const int max = 200;
        string s = v switch
        {
            null => "None",
            string str => str,
            bool b => b ? "True" : "False",
            ClassDict cd => $"<{cd.ClassName}>",
            IDictionary d => $"{{{d.Count} Einträge}}",
            object?[] arr => $"({arr.Length} Elemente)",
            ArrayList al => $"[{al.Count} Elemente]",
            IList il => $"[{il.Count} Elemente]",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString() ?? "",
        };
        return s.Length > max ? s[..max] + "…" : s;
    }

    // ---- Zip-Helfer --------------------------------------------------------

    private static byte[]? ReadEntryBytes(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null) return null;
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
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

    // ---- Bekannte Ren'Py-Constructors registrieren --------------------------

    /// <summary>Registriert Passthrough-Constructors für Ren'Py-Container, die per
    /// __reduce__ mit Args serialisiert werden. Idempotent (statisch, aber
    /// registerConstructor überschreibt existierende Einträge stumm).</summary>
    private static void EnsureConstructorsRegistered()
    {
        if (Interlocked.Exchange(ref _constructorsRegistered, 1) == 1) return;

        // RevertableDict/List/Set gibt es in zwei Modulpfaden (Ren'Py 7 vs. 8).
        foreach (string mod in new[] { "renpy.revertable", "renpy.python" })
        {
            Unpickler.registerConstructor(mod, "RevertableDict", new PassthroughConstructor(mod, "RevertableDict"));
            Unpickler.registerConstructor(mod, "RevertableList", new PassthroughConstructor(mod, "RevertableList"));
            Unpickler.registerConstructor(mod, "RevertableSet",  new PassthroughConstructor(mod, "RevertableSet"));
            Unpickler.registerConstructor(mod, "RevertableObject", new PassthroughConstructor(mod, "RevertableObject"));
        }
    }

    /// <summary>Passthrough-Constructor: erzeugt eine <see cref="ClassDict"/>-artige
    /// Struktur ohne die Constructor-Argumente zu verlieren. Ein nachfolgendes
    /// BUILD (setstate) füllt das Dict weiter; die Args landen unter dem Key
    /// "__args__" für spätere Auswertung.</summary>
    private sealed class PassthroughConstructor(string module, string name) : IObjectConstructor
    {
        public object construct(object[] args)
        {
            var cd = new ClassDict(module, name);
            if (args.Length > 0) cd["__args__"] = args;
            return cd;
        }
    }
}
