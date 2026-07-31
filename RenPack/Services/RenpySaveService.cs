using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
///     - "log"         Pickle (protocol 2–5) von (roots, log). In neueren Ren'Py-
///                     Versionen NICHT mehr zlib-komprimiert (0x80 = Pickle-Proto);
///                     alte Saves sind zlib (0x78).
///     - "json"        JSON-Metadaten (save_name, save_time, renpy_version …)
///     - "screenshot.png"  PNG-Vorschau
///     - "signatures"  (optional) HMAC-Signaturen
///     - "renpy_version" ASCII-Version
///     - "extra_info"  (optional)
///
/// Der <c>roots</c>-Dict enthält direkt die vollqualifizierten Store-Variablen
/// (Keys wie <c>store.money</c>, <c>store._menu</c>, <c>persistent.foo</c>) — kein
/// verschachtelter Namespace-Wrapper.
/// </summary>
public sealed class RenpySaveService : IRenpySaveService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static int _initDone;

    public RenpySaveService() => EnsureInitialized();

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
                SaveTime: TryReadUnix(raw, "_save_time") ?? TryReadUnix(raw, "_ctime"),
                RenpyVersion: FormatVersion(raw),
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

    private static string? FormatVersion(IDictionary<string, object?> raw)
    {
        if (!raw.TryGetValue("_renpy_version", out var v) || v is null) return null;
        // Ren'Py 8 speichert die Version als List [major, minor, patch, build].
        if (v is IEnumerable<object?> list && v is not string)
            return string.Join(".", list.Select(x => x?.ToString() ?? ""));
        return v.ToString();
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

        // Ältere Ren'Py-Versionen zlib-komprimieren den Log (beginnt mit 0x78),
        // neuere schreiben rohes Pickle (0x80 = Protocol-Marker).
        byte[] pickle = logBytes.Length > 0 && logBytes[0] == 0x78
            ? ZlibDecompress(logBytes) : logBytes;

        using var u = new Unpickler();
        return u.loads(pickle);
    }

    // ---- Store-Extraktion --------------------------------------------------

    /// <summary>Sucht die Store-Variablen im entpickleten Log. Ren'Py 8.x:
    /// Top-Level ist ein Tupel <c>(roots, log)</c>, <c>roots</c> ist ein Dict
    /// mit voll qualifizierten Keys (<c>store.foo</c>, <c>persistent.bar</c>).
    /// Ältere Formate mit verschachteltem Namespace-Wrapper werden ebenfalls
    /// unterstützt.</summary>
    private static IReadOnlyList<SaveVariable> ExtractVariables(object? logRoot)
    {
        if (logRoot is not object?[] { Length: >= 1 } arr) return [];
        if (arr[0] is not IDictionary roots) return [];

        // Fall 1 (Ren'Py 8.x): roots enthält "store.foo"-Keys direkt.
        var flat = new List<SaveVariable>();
        bool hasQualifiedKeys = false;
        foreach (DictionaryEntry de in roots)
        {
            if (de.Key is not string key) continue;
            if (key.StartsWith("store.", StringComparison.Ordinal))
            {
                hasQualifiedKeys = true;
                string name = key["store.".Length..];
                flat.Add(new SaveVariable(name, TypeDisplayName(de.Value),
                    ValueDisplay(de.Value), name.StartsWith('_')));
            }
        }
        if (hasQualifiedKeys)
        {
            flat.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            return flat;
        }

        // Fall 2 (ältere Formate): roots["store"] ist selbst ein Dict.
        if (roots.Contains("store") && roots["store"] is IDictionary storeDict)
            return DictToVariables(storeDict);

        // Fall 3: roots IST direkt der Store (sehr alte Formate).
        return DictToVariables(roots);
    }

    private static IReadOnlyList<SaveVariable> DictToVariables(IDictionary d)
    {
        var result = new List<SaveVariable>(d.Count);
        foreach (DictionaryEntry de in d)
        {
            string key = de.Key?.ToString() ?? "";
            result.Add(new SaveVariable(key, TypeDisplayName(de.Value),
                ValueDisplay(de.Value), key.StartsWith('_')));
        }
        result.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
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

    // ---- Razorvine.Pickle-Erweiterung: Catch-all für unbekannte Klassen ----

    /// <summary>Statischer Einmal-Setup: patcht die interne
    /// <c>Unpickler.objectConstructors</c>-Map mit einem <see cref="CatchAllDict"/>-
    /// Wrapper, sodass jede unbekannte Klasse einen tolerantten Passthrough-
    /// Constructor bekommt (statt <see cref="PickleException"/>). Registriert
    /// zusätzlich echte Container für <c>RevertableDict/List/Set</c>, weil diese
    /// per <c>__reduce__</c>+APPENDS/SETITEMS aufgebaut werden.
    ///
    /// Das readonly-Statik-Feld wird per <see cref="UnsafeAccessor"/> ersetzt
    /// (.NET 8+); anders geht es nicht ohne Razorvine zu forken.</summary>
    private static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initDone, 1) == 1) return;

        ref var slot = ref Accessors.ObjectConstructors();
        slot = new CatchAllDict(slot);

        foreach (string mod in new[] { "renpy.revertable", "renpy.python" })
        {
            Unpickler.registerConstructor(mod, "RevertableDict", new RevertableDictCtor());
            Unpickler.registerConstructor(mod, "RevertableList", new RevertableListCtor());
            Unpickler.registerConstructor(mod, "RevertableSet",  new RevertableListCtor());
        }
    }

    private static class Accessors
    {
        [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "objectConstructors")]
        public static extern ref IDictionary<string, IObjectConstructor> ObjectConstructors(Unpickler? _ = null);
    }

    /// <summary>Wrapper-Dictionary: liefert für unbekannte Keys immer einen
    /// <see cref="OpaqueCtor"/> statt PickleException. Die Namensauflösung
    /// erfolgt beim <see cref="TryGetValue"/>-Aufruf aus dem
    /// "module.classname"-Key.</summary>
    private sealed class CatchAllDict(IDictionary<string, IObjectConstructor> inner)
        : IDictionary<string, IObjectConstructor>
    {
        public IObjectConstructor this[string key]
        {
            get => inner.TryGetValue(key, out var v) ? v : MakeOpaque(key);
            set => inner[key] = value;
        }
        public ICollection<string> Keys => inner.Keys;
        public ICollection<IObjectConstructor> Values => inner.Values;
        public int Count => inner.Count;
        public bool IsReadOnly => inner.IsReadOnly;
        public void Add(string k, IObjectConstructor v) => inner.Add(k, v);
        public void Add(KeyValuePair<string, IObjectConstructor> item) => inner.Add(item);
        public void Clear() => inner.Clear();
        public bool Contains(KeyValuePair<string, IObjectConstructor> item) => inner.Contains(item);
        public bool ContainsKey(string key) => true;
        public void CopyTo(KeyValuePair<string, IObjectConstructor>[] a, int i) => inner.CopyTo(a, i);
        public IEnumerator<KeyValuePair<string, IObjectConstructor>> GetEnumerator() => inner.GetEnumerator();
        public bool Remove(string key) => inner.Remove(key);
        public bool Remove(KeyValuePair<string, IObjectConstructor> item) => inner.Remove(item);
        public bool TryGetValue(string key, out IObjectConstructor value)
        {
            if (inner.TryGetValue(key, out value!)) return true;
            value = MakeOpaque(key);
            return true;
        }
        IEnumerator IEnumerable.GetEnumerator() => inner.GetEnumerator();

        private static IObjectConstructor MakeOpaque(string qualifiedKey)
        {
            int dot = qualifiedKey.LastIndexOf('.');
            var mod = dot > 0 ? qualifiedKey[..dot] : "";
            var cls = dot > 0 ? qualifiedKey[(dot + 1)..] : qualifiedKey;
            return new OpaqueCtor(mod, cls);
        }
    }

    /// <summary>Constructor für unbekannte Klassen: erzeugt ein
    /// <see cref="RenpyOpaqueDict"/> mit multi-signature <c>__setstate__</c>.</summary>
    private sealed class OpaqueCtor(string module, string name) : IObjectConstructor
    {
        public object construct(object[] args) => new RenpyOpaqueDict(module, name, args);
    }

    /// <summary><see cref="ClassDict"/>-Ableger, der SOWOHL <c>Hashtable</c>- als auch
    /// <c>object[]</c>- und <c>object</c>-States entgegennimmt. Nötig, weil viele
    /// Ren'Py-Klassen ihren State als Tuple statt Dict serialisieren und der
    /// Standard-<see cref="ClassDict"/> dann per Reflection kein passendes
    /// <c>__setstate__</c> findet.</summary>
    private sealed class RenpyOpaqueDict : ClassDict
    {
        public RenpyOpaqueDict(string module, string name, object[] args) : base(module, name)
        {
            if (args.Length > 0) this["__args__"] = args;
        }
        public new void __setstate__(Hashtable state) => base.__setstate__(state);
        public void __setstate__(object[] state) { this["__state__"] = state; }
        public void __setstate__(object state) { this["__state__"] = state; }
    }

    private sealed class RevertableDictCtor : IObjectConstructor
    {
        public object construct(object[] args)
        {
            var d = new OpaqueHashtable();
            // RevertableDict.__reduce_ex__ liefert typischerweise (list_of_items,).
            if (args.Length >= 1 && args[0] is IList items)
                foreach (var item in items)
                    if (item is object[] { Length: 2 } pair && pair[0] is not null)
                        d[pair[0]] = pair[1];
            return d;
        }
    }

    private sealed class RevertableListCtor : IObjectConstructor
    {
        public object construct(object[] args)
        {
            var l = new OpaqueArrayList();
            if (args.Length >= 1 && args[0] is IEnumerable src && args[0] is not string)
                foreach (var i in src) l.Add(i);
            return l;
        }
    }

    /// <summary><see cref="Hashtable"/>-Ableger mit tolerantem <c>__setstate__</c>
    /// (Hashtable/object[]/object) — sonst schluckt Razorvine bei BUILD.</summary>
    private sealed class OpaqueHashtable : Hashtable
    {
        public void __setstate__(Hashtable state)
        {
            foreach (DictionaryEntry de in state) this[de.Key] = de.Value;
        }
        public void __setstate__(object[] state) { }
        public void __setstate__(object state) { }
    }

    /// <summary><see cref="ArrayList"/>-Ableger mit tolerantem <c>__setstate__</c>
    /// (analog <see cref="OpaqueHashtable"/>).</summary>
    private sealed class OpaqueArrayList : ArrayList
    {
        public void __setstate__(Hashtable state) { }
        public void __setstate__(object[] state) { }
        public void __setstate__(object state) { }
    }
}
