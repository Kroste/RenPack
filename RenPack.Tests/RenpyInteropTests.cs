using System.Diagnostics;
using System.Security.Cryptography;
using FluentAssertions;
using RenPack.Services;
using Xunit;

namespace RenPack.Tests;

/// <summary>
/// Beweist echte Ren'Py-Kompatibilität: Ein mit Python (zlib+pickle, exakt wie Ren'Py)
/// erzeugtes Archiv muss RenPack lesen — und ein von RenPack erzeugtes Archiv muss Python
/// wieder auslesen können. Ohne verfügbares python3 werden die Tests übersprungen.
/// </summary>
public sealed class RenpyInteropTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "renpack-interop-" + Guid.NewGuid().ToString("N"));
    private readonly RenpyArchiveService _svc = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public RenpyInteropTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, recursive: true); } catch { /* egal */ } }

    // Python-Writer im Ren'Py-Stil (RPA-3.0).
    private const string PyWrite = """
        import sys, os, zlib, pickle
        src, out = sys.argv[1], sys.argv[2]
        key = 0xDEADBEEF
        with open(out, 'wb') as f:
            f.write(b' ' * 34)  # Platzhalter fuer "RPA-3.0 %016x %08x\n"
            index = {}
            for root, _, names in os.walk(src):
                for n in names:
                    p = os.path.join(root, n)
                    rel = os.path.relpath(p, src).replace(os.sep, '/')
                    offset = f.tell()
                    data = open(p, 'rb').read()
                    f.write(data)
                    index[rel] = [(offset ^ key, len(data) ^ key)]
            index_offset = f.tell()
            f.write(zlib.compress(pickle.dumps(index, protocol=2)))
            f.seek(0)
            f.write(('RPA-3.0 %016x %08x\n' % (index_offset, key)).encode('ascii'))
        """;

    // Python-Reader im Ren'Py-Stil (deobfuskiert und entpackt).
    private const string PyRead = """
        import sys, os, zlib, pickle
        arc, outdir = sys.argv[1], sys.argv[2]
        with open(arc, 'rb') as f:
            parts = f.readline().split()
            tag = parts[0]
            if tag == b'RPA-3.0':
                offset = int(parts[1], 16); key = int(parts[2], 16)
            elif tag == b'RPA-3.2':
                offset = int(parts[1], 16); key = int(parts[3], 16)
            elif tag == b'RPA-2.0':
                offset = int(parts[1], 16); key = 0
            else:
                raise SystemExit('unknown ' + repr(tag))
            f.seek(offset)
            index = pickle.loads(zlib.decompress(f.read()))
            for name, entries in index.items():
                if isinstance(name, bytes):
                    name = name.decode('utf-8')
                blob = b''
                for e in entries:
                    if len(e) == 2:
                        o, l = e; pre = b''
                    else:
                        o, l, pre = e
                        if isinstance(pre, str): pre = pre.encode('latin1')
                    o ^= key; l ^= key
                    f.seek(o)
                    blob += pre + f.read(l - len(pre))
                dst = os.path.join(outdir, name)
                d = os.path.dirname(dst)
                if d: os.makedirs(d, exist_ok=True)
                open(dst, 'wb').write(blob)
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
            catch { /* nächster Kandidat */ }
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

    private Dictionary<string, byte[]> MakeTree(string src)
    {
        Directory.CreateDirectory(src);
        var files = new Dictionary<string, byte[]>
        {
            ["a.rpy"] = "label start:\n    pass\n"u8.ToArray(),
            ["sub/b.dat"] = RandomNumberGenerator.GetBytes(9000),
            ["c_äöü.txt"] = "Grüße 🎮"u8.ToArray(),
            ["leer.bin"] = [],
        };
        foreach (var (rel, data) in files)
        {
            var full = Path.Combine(src, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, data);
        }
        return files;
    }

    [Fact]
    public void RenPack_reads_archive_created_by_python()
    {
        var py = PythonExe();
        if (py is null) { Assert.Skip("python3 nicht verfügbar"); return; }

        var src = Path.Combine(_tmp, "src");
        var files = MakeTree(src);
        var archive = Path.Combine(_tmp, "py.rpa");
        RunPython(py, PyWrite, src, archive);

        var info = _svc.ReadIndex(archive);
        info.Version.Should().Be(RpaVersion.V3_0);
        info.Entries.Select(e => e.Path).Should().BeEquivalentTo(files.Keys);

        var dest = Path.Combine(_tmp, "out");
        _svc.ExtractAll(info, dest, cancellationToken: Ct);
        foreach (var (rel, data) in files)
        {
            var full = Path.Combine(dest, rel.Replace('/', Path.DirectorySeparatorChar));
            File.ReadAllBytes(full).Should().Equal(data, $"{rel}");
        }
    }

    [Fact]
    public void Python_reads_archive_created_by_RenPack()
    {
        var py = PythonExe();
        if (py is null) { Assert.Skip("python3 nicht verfügbar"); return; }

        var src = Path.Combine(_tmp, "src");
        var files = MakeTree(src);
        var archive = Path.Combine(_tmp, "renpack.rpa");
        _svc.Create(archive, src, RpaVersion.V3_0, cancellationToken: Ct);

        var dest = Path.Combine(_tmp, "pyout");
        Directory.CreateDirectory(dest);
        RunPython(py, PyRead, archive, dest);

        foreach (var (rel, data) in files)
        {
            var full = Path.Combine(dest, rel.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(full).Should().BeTrue($"Python sollte {rel} entpacken");
            File.ReadAllBytes(full).Should().Equal(data, $"{rel}");
        }
    }
}
