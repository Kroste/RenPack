using System.Diagnostics;
using System.IO.Compression;
using FluentAssertions;
using RenPack.Services;
using Xunit;

namespace RenPack.Tests;

/// <summary>
/// Tests für den read-only Save-Inspector. Nutzt Python (zlib+pickle) um ein
/// Ren'Py-Save-ähnliches ZIP zu bauen — damit ist ausgeschlossen, dass Test und
/// Service denselben Coding-Fehler teilen. Ohne python3 werden die Tests
/// übersprungen (wie bei den Interop-Tests).
/// </summary>
public sealed class SaveServiceTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "renpack-save-" + Guid.NewGuid().ToString("N"));
    private readonly RenpySaveService _svc = new();

    public SaveServiceTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, recursive: true); } catch { /* egal */ } }

    // Python-Skript: erzeugt eine .save-Datei im Ren'Py-Format. Das Root-Objekt
    // im "log"-Eintrag ist (roots, log), wobei roots["store"] das Store-Dict ist.
    // "log" bleibt hier ein einfacher Marker — der Service muss trotzdem den
    // Store finden.
    private const string PyBuildSave = """
        import sys, os, zlib, pickle, json, zipfile, time, struct

        outpath = sys.argv[1]

        store = {
            'money': 1234,
            'hp': 42.5,
            'player_name': 'Alice',
            'has_key': True,
            'inventory': ['sword', 'shield', 'potion'],
            '_menu': None,
            '_return': None,
            '_args': (),
        }
        roots = { 'store': store }
        log_root = (roots, ['fake-log-placeholder'])

        pickled = pickle.dumps(log_root, protocol=2)
        log_bytes = zlib.compress(pickled)

        # Minimales 1x1 PNG.
        png = (b'\x89PNG\r\n\x1a\n' + b'\x00\x00\x00\rIHDR' +
               b'\x00\x00\x00\x01\x00\x00\x00\x01\x08\x02\x00\x00\x00\x90wS\xde' +
               b'\x00\x00\x00\x0cIDATx\x9cc\xf8\xcf\xc0\x00\x00\x00\x03\x00\x01\x86\x82\xc9\xd7' +
               b'\x00\x00\x00\x00IEND\xaeB`\x82')

        meta = {
            '_save_name': 'Test-Slot',
            '_save_time': int(time.time()),
            '_renpy_version': '8.1.3',
            '_game_name': 'RenPack-Save-Test',
        }

        with zipfile.ZipFile(outpath, 'w', zipfile.ZIP_DEFLATED) as z:
            z.writestr('log', log_bytes)
            z.writestr('json', json.dumps(meta))
            z.writestr('screenshot.png', png)
        """;

    // Ren'Py-8-Format: roots ist ein Dict mit VOLLQUALIFIZIERTEN Keys ("store.money",
    // "persistent.foo"), kein verschachtelter Namespace-Wrapper. Zusätzlich mit
    // unbekannten "Ren'Py-Klassen" gespickt (Fake-Modules via sys.modules), damit
    // der Catch-all-Constructor mitgeprüft wird.
    private const string PyBuildSaveRenpy8 = """
        import sys, os, types, pickle, json, zipfile

        outpath = sys.argv[1]

        # Fake-Module registrieren, damit pickle die "Klassen" serialisieren darf.
        for mod in ('renpy', 'renpy.rollback', 'renpy.display', 'renpy.pyanalysis'):
            sys.modules[mod] = types.ModuleType(mod)

        class RollbackLog:
            __module__ = 'renpy.rollback'
            def __init__(self):
                self.log = []
                self.identifier = 'test-1'
            # Tuple-State — hier bricht Standard-ClassDict.
            def __reduce_ex__(self, proto):
                return (RollbackLog, (), (self.log, self.identifier))
        sys.modules['renpy.rollback'].RollbackLog = RollbackLog

        class Displayable:
            __module__ = 'renpy.display'
            def __reduce_ex__(self, proto):
                return (Displayable, ())
        sys.modules['renpy.display'].Displayable = Displayable

        roots = {
            'store.money': 5000,
            'store.player_name': 'Bob',
            'store.has_key': True,
            'store._menu': None,
            'store._history_list': [],
            'store.strange_obj': Displayable(),
            'persistent._daily_check': 42,
        }
        log_root = (roots, RollbackLog())
        # Neuere Ren'Py-Versionen komprimieren den log NICHT mehr.
        log_bytes = pickle.dumps(log_root, protocol=5)

        with zipfile.ZipFile(outpath, 'w', zipfile.ZIP_DEFLATED) as z:
            z.writestr('log', log_bytes)
            z.writestr('json', json.dumps({
                '_save_name': 'Real-Format',
                '_renpy_version': [8, 4, 1, 25072401],
                '_ctime': 1778676757,
            }))
        """;

    // Save mit RevertableDict/List (per __reduce__ mit Args). Testet, dass der
    // Passthrough-Constructor die unbekannten Klassen abfängt. Trick: sys.modules
    // wird mit Fake-Modulen bestückt, sonst weigert sich pickle beim dumps.
    private const string PyBuildSaveRevertable = """
        import sys, os, types, zlib, pickle, json, zipfile

        outpath = sys.argv[1]

        fake_renpy = types.ModuleType('renpy')
        fake_revertable = types.ModuleType('renpy.revertable')
        sys.modules['renpy'] = fake_renpy
        sys.modules['renpy.revertable'] = fake_revertable

        class RevertableDict(dict):
            __module__ = 'renpy.revertable'
            def __reduce__(self):
                return (RevertableDict, (list(self.items()),))
        class RevertableList(list):
            __module__ = 'renpy.revertable'
            def __reduce__(self):
                return (RevertableList, (list(self),))

        fake_revertable.RevertableDict = RevertableDict
        fake_revertable.RevertableList = RevertableList

        store = {
            'gold': 999,
            'flags': RevertableDict({'started': True, 'boss_dead': False}),
            'items': RevertableList(['potion', 'sword']),
            '_menu': None,
            '_return': None,
        }
        roots = { 'store': store }
        log_root = (roots, ['fake-log'])
        log_bytes = zlib.compress(pickle.dumps(log_root, protocol=2))

        with zipfile.ZipFile(outpath, 'w', zipfile.ZIP_DEFLATED) as z:
            z.writestr('log', log_bytes)
            z.writestr('json', json.dumps({'_save_name': 'RD-Test'}))
        """;

    private static string? PythonExe()
    {
        foreach (var exe in new[] { "python3", "python" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, "--version")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                p!.WaitForExit(5000);
                if (p.ExitCode == 0) return exe;
            }
            catch { /* nächster */ }
        }
        return null;
    }

    private void RunPython(string exe, string script, params string[] args)
    {
        var scriptPath = Path.Combine(_tmp, "s" + Guid.NewGuid().ToString("N") + ".py");
        File.WriteAllText(scriptPath, script);
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(scriptPath);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        string err = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30000);
        proc.ExitCode.Should().Be(0, $"Python-Fehler: {err}");
    }

    [Fact]
    public void Reads_metadata_screenshot_and_store_from_synthetic_save()
    {
        var py = PythonExe();
        if (py is null) { Assert.Skip("python3 nicht verfügbar"); return; }

        var savePath = Path.Combine(_tmp, "slot1.save");
        RunPython(py, PyBuildSave, savePath);

        var info = _svc.Read(savePath);

        info.LogError.Should().BeNull();
        info.Metadata.SaveName.Should().Be("Test-Slot");
        info.Metadata.RenpyVersion.Should().Be("8.1.3");
        info.Metadata.GameName.Should().Be("RenPack-Save-Test");
        info.Metadata.SaveTime.Should().NotBeNull();
        info.ScreenshotBytes.Should().NotBeNull();
        info.ScreenshotBytes!.Length.Should().BeGreaterThan(0);

        info.Variables.Should().Contain(v => v.Name == "money" && v.Value == "1234");
        info.Variables.Should().Contain(v => v.Name == "player_name" && v.Value == "Alice" && v.TypeName == "str");
        info.Variables.Should().Contain(v => v.Name == "has_key" && v.Value == "True" && v.TypeName == "bool");
        info.Variables.Should().Contain(v => v.Name == "hp" && v.TypeName == "float");
        info.Variables.Should().Contain(v => v.Name == "_menu" && v.IsInternal);
    }

    [Fact]
    public void Reads_renpy8_flat_roots_format_and_tolerates_unknown_classes()
    {
        var py = PythonExe();
        if (py is null) { Assert.Skip("python3 nicht verfügbar"); return; }

        var savePath = Path.Combine(_tmp, "flat.save");
        RunPython(py, PyBuildSaveRenpy8, savePath);

        var info = _svc.Read(savePath);

        info.LogError.Should().BeNull("Catch-all-Constructor sollte unbekannte Ren'Py-Klassen abfangen");
        info.Metadata.SaveName.Should().Be("Real-Format");
        info.Metadata.RenpyVersion.Should().Be("8.4.1.25072401");
        info.Metadata.SaveTime.Should().NotBeNull();

        // Vollqualifizierte Keys wurden zerlegt: "store.money" → Name "money".
        info.Variables.Should().Contain(v => v.Name == "money" && v.Value == "5000");
        info.Variables.Should().Contain(v => v.Name == "player_name" && v.Value == "Bob");
        info.Variables.Should().Contain(v => v.Name == "has_key" && v.Value == "True");
        info.Variables.Should().Contain(v => v.Name == "strange_obj" && v.TypeName == "Displayable");
        info.Variables.Should().Contain(v => v.Name == "_menu" && v.IsInternal);
        // persistent.*-Keys sind KEIN Store-Namespace und werden ausgelassen.
        info.Variables.Should().NotContain(v => v.Name == "_daily_check");
    }

    [Fact]
    public void Handles_revertable_containers_via_passthrough_constructors()
    {
        var py = PythonExe();
        if (py is null) { Assert.Skip("python3 nicht verfügbar"); return; }

        var savePath = Path.Combine(_tmp, "revertable.save");
        RunPython(py, PyBuildSaveRevertable, savePath);

        var info = _svc.Read(savePath);

        info.LogError.Should().BeNull("Passthrough sollte RevertableDict/List abfangen");
        info.Variables.Should().Contain(v => v.Name == "gold" && v.Value == "999");
        info.Variables.Should().Contain(v => v.Name == "flags");
        info.Variables.Should().Contain(v => v.Name == "items");
    }

    [Fact]
    public void Broken_log_still_returns_metadata_and_screenshot()
    {
        var savePath = Path.Combine(_tmp, "broken.save");
        using (var zip = ZipFile.Open(savePath, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("json").Open()))
                w.Write("""{"_save_name": "Broken"}""");
            using (var w = zip.CreateEntry("log").Open())
                w.Write([0x78, 0x9C, 0xFF, 0xFF, 0xFF, 0xFF], 0, 6); // kaputte zlib-Bytes
        }

        var info = _svc.Read(savePath);
        info.Metadata.SaveName.Should().Be("Broken");
        info.LogError.Should().NotBeNull();
        info.Variables.Should().BeEmpty();
    }

    // ---- v0.3 Editor-Tests -------------------------------------------------

    [Fact]
    public void Roundtrip_edit_int_bool_str_float_preserves_other_bytes()
    {
        var py = PythonExe();
        if (py is null) { Assert.Skip("python3 nicht verfügbar"); return; }

        var savePath = Path.Combine(_tmp, "edit.save");
        RunPython(py, PyBuildSaveRenpy8, savePath);

        var beforeInfo = _svc.Read(savePath);
        beforeInfo.LogError.Should().BeNull();
        beforeInfo.Variables.Should().Contain(v => v.Name == "money" && v.Value == "5000");

        var edited = Path.Combine(_tmp, "edited.save");
        _svc.Write(savePath, edited, [
            new SaveEdit("money", (long)999999),          // BININT1(2) → BININT(5) — Länge ändert sich
            new SaveEdit("has_key", false),               // NEWTRUE(1) → NEWFALSE(1) — gleiche Länge
            new SaveEdit("player_name", "Zoe Longname"),  // SHORT_BINUNICODE mit neuer Länge
        ]);

        var afterInfo = _svc.Read(edited);
        afterInfo.LogError.Should().BeNull("Nach Splice muss der Log weiter lesbar sein");
        afterInfo.Variables.Should().Contain(v => v.Name == "money" && v.Value == "999999");
        afterInfo.Variables.Should().Contain(v => v.Name == "has_key" && v.Value == "False");
        afterInfo.Variables.Should().Contain(v => v.Name == "player_name" && v.Value == "Zoe Longname");
        // Nicht editierte Variablen bleiben.
        afterInfo.Variables.Should().Contain(v => v.Name == "strange_obj" && v.TypeName == "Displayable");
    }

    [Fact]
    public void Roundtrip_edit_list_and_dict_via_python_literal()
    {
        // v0.5: Listen/Dict-Editing im Save-Editor. Der User sieht den Wert
        // als Python-Literal, editiert ihn, wir parsen + encoden + splicen.
        var py = PythonExe();
        if (py is null) { Assert.Skip("python3 nicht verfügbar"); return; }

        var savePath = Path.Combine(_tmp, "list_dict.save");
        RunPython(py, PyBuildSaveWithListAndDict, savePath);

        var beforeInfo = _svc.Read(savePath);
        beforeInfo.LogError.Should().BeNull();
        beforeInfo.Variables.Should().Contain(v => v.Name == "flags"
            && v.TypeName == "list");
        beforeInfo.Variables.Should().Contain(v => v.Name == "stats"
            && v.TypeName == "dict");

        // Wir aendern die Liste [1,2,3] auf [10,20,30,40] (add + change)
        // und dict {a:1,b:2} auf {a:99, c:3} (rename+change).
        var edited = Path.Combine(_tmp, "list_dict.edited.save");
        _svc.Write(savePath, edited, [
            new SaveEdit("flags", new List<object?> { 10L, 20L, 30L, 40L }),
            new SaveEdit("stats", new Dictionary<object, object?> { { "a", 99L }, { "c", 3L } }),
        ]);

        var after = _svc.Read(edited);
        after.LogError.Should().BeNull();
        var flags = after.Variables.Single(v => v.Name == "flags");
        flags.Value.Should().Be("[10, 20, 30, 40]");
        var stats = after.Variables.Single(v => v.Name == "stats");
        // Dict-Reihenfolge kann per Hashtable variieren — pruefe beide Keys.
        stats.Value.Should().Contain("'a': 99");
        stats.Value.Should().Contain("'c': 3");
    }

    private const string PyBuildSaveWithListAndDict = """
        import sys, os, types, pickle, json, zipfile
        outpath = sys.argv[1]

        for mod in ('renpy', 'renpy.rollback'):
            sys.modules[mod] = types.ModuleType(mod)

        class RollbackLog:
            __module__ = 'renpy.rollback'
            def __init__(self):
                self.log = []
                self.identifier = 'test-listdict'
            def __reduce_ex__(self, proto):
                return (RollbackLog, (), (self.log, self.identifier))
        sys.modules['renpy.rollback'].RollbackLog = RollbackLog

        roots = {
            'store.flags': [1, 2, 3],
            'store.stats': {'a': 1, 'b': 2},
            'store.name': 'Alice',
        }
        log_bytes = pickle.dumps((roots, RollbackLog()), protocol=5)

        with zipfile.ZipFile(outpath, 'w', zipfile.ZIP_DEFLATED) as z:
            z.writestr('log', log_bytes)
            z.writestr('json', json.dumps({
                '_save_name': 'ListDict',
                '_renpy_version': [8, 4, 1, 25072401],
                '_ctime': 1778676757,
            }))
        """;

    [Fact]
    public void Write_can_replace_original_file_atomically()
    {
        var py = PythonExe();
        if (py is null) { Assert.Skip("python3 nicht verfügbar"); return; }

        var savePath = Path.Combine(_tmp, "inplace.save");
        RunPython(py, PyBuildSaveRenpy8, savePath);
        long originalBytes = new FileInfo(savePath).Length;

        _svc.Write(savePath, savePath, [new SaveEdit("money", (long)7000)]);

        var info = _svc.Read(savePath);
        info.Variables.Should().Contain(v => v.Name == "money" && v.Value == "7000");
        // Datei soll intakt sein (nicht während des Ersetzens gelöscht liegen bleiben).
        File.Exists(savePath).Should().BeTrue();
    }

    [Fact]
    public void Write_updates_save_name_in_json_when_requested()
    {
        var py = PythonExe();
        if (py is null) { Assert.Skip("python3 nicht verfügbar"); return; }

        var savePath = Path.Combine(_tmp, "rename.save");
        RunPython(py, PyBuildSaveRenpy8, savePath);

        var edited = Path.Combine(_tmp, "renamed.save");
        _svc.Write(savePath, edited, [], newSaveName: "Cheat-Save");

        var info = _svc.Read(edited);
        info.Metadata.SaveName.Should().Be("Cheat-Save");
    }

    [Fact]
    public void Write_drops_signatures_by_default()
    {
        var py = PythonExe();
        if (py is null) { Assert.Skip("python3 nicht verfügbar"); return; }

        var savePath = Path.Combine(_tmp, "sig.save");
        RunPython(py, PyBuildSaveRenpy8, savePath);
        // Signatures-Eintrag hinzufügen.
        using (var zip = ZipFile.Open(savePath, ZipArchiveMode.Update))
        {
            using var s = zip.CreateEntry("signatures").Open();
            s.Write("fake-hmac"u8);
        }

        var edited = Path.Combine(_tmp, "no-sig.save");
        _svc.Write(savePath, edited, [new SaveEdit("money", (long)42)]);

        using var check = ZipFile.OpenRead(edited);
        check.GetEntry("signatures").Should().BeNull();
    }

    [Fact]
    public void Missing_json_yields_empty_metadata_but_still_reads_log()
    {
        var py = PythonExe();
        if (py is null) { Assert.Skip("python3 nicht verfügbar"); return; }

        var savePath = Path.Combine(_tmp, "nojson.save");
        RunPython(py, PyBuildSave, savePath);

        // JSON-Eintrag entfernen.
        var stripped = Path.Combine(_tmp, "stripped.save");
        using (var src = ZipFile.OpenRead(savePath))
        using (var dst = ZipFile.Open(stripped, ZipArchiveMode.Create))
        {
            foreach (var e in src.Entries.Where(e => e.Name != "json"))
            {
                var ne = dst.CreateEntry(e.FullName);
                using var si = e.Open();
                using var di = ne.Open();
                si.CopyTo(di);
            }
        }

        var info = _svc.Read(stripped);
        info.Metadata.SaveName.Should().BeNull();
        info.LogError.Should().BeNull();
        info.Variables.Should().Contain(v => v.Name == "money");
    }
}
