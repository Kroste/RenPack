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
