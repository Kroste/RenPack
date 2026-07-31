using System.Security.Cryptography;
using FluentAssertions;
using RenPack.Services;
using Xunit;

namespace RenPack.Tests;

/// <summary>Round-Trip und Formatdetails der RPA-Kernlogik (hermetisch, ohne Python).</summary>
public sealed class RpaRoundTripTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "renpack-test-" + Guid.NewGuid().ToString("N"));
    private readonly RenpyArchiveService _svc = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public RpaRoundTripTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, recursive: true); } catch { /* egal */ } }

    private (string src, Dictionary<string, byte[]> files) MakeSampleTree()
    {
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);
        var files = new Dictionary<string, byte[]>
        {
            ["script.rpy"] = "label start:\n    \"Hallo Welt\"\n"u8.ToArray(),
            ["images/bg.txt"] = RandomNumberGenerator.GetBytes(5000),
            ["gui/leer.dat"] = [],                          // leere Datei
            ["audio/ton.bin"] = RandomNumberGenerator.GetBytes(131072),
            ["umlaut_äöü.txt"] = "Grüße 🎮"u8.ToArray(),     // Unicode-Name + Inhalt
        };
        foreach (var (rel, data) in files)
        {
            var full = Path.Combine(src, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, data);
        }
        return (src, files);
    }

    [Theory]
    [InlineData(RpaVersion.V3_0)]
    [InlineData(RpaVersion.V2_0)]
    [InlineData(RpaVersion.V3_2)]
    public void Create_Read_Extract_roundtrips_all_files(RpaVersion version)
    {
        var (src, files) = MakeSampleTree();
        var archivePath = Path.Combine(_tmp, "out.rpa");

        int packed = _svc.Create(archivePath, src, version, cancellationToken: Ct);
        packed.Should().Be(files.Count);

        var info = _svc.ReadIndex(archivePath);
        info.Version.Should().Be(version);
        info.Entries.Should().HaveCount(files.Count);
        info.Entries.Select(e => e.Path).Should().BeEquivalentTo(files.Keys);

        var dest = Path.Combine(_tmp, "out");
        int extracted = _svc.ExtractAll(info, dest, cancellationToken: Ct);
        extracted.Should().Be(files.Count);

        foreach (var (rel, data) in files)
        {
            var full = Path.Combine(dest, rel.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(full).Should().BeTrue($"{rel} sollte entpackt sein");
            File.ReadAllBytes(full).Should().Equal(data, $"Inhalt von {rel} muss identisch sein");
        }
    }

    [Fact]
    public void Extract_selected_only_writes_chosen_files()
    {
        var (src, _) = MakeSampleTree();
        var archivePath = Path.Combine(_tmp, "out.rpa");
        _svc.Create(archivePath, src, cancellationToken: Ct);
        var info = _svc.ReadIndex(archivePath);

        var chosen = info.Entries.Where(e => e.Path.EndsWith(".rpy")).ToList();
        var dest = Path.Combine(_tmp, "sel");
        int n = _svc.Extract(info, chosen, dest, cancellationToken: Ct);

        n.Should().Be(chosen.Count);
        Directory.GetFiles(dest, "*", SearchOption.AllDirectories).Should().HaveCount(chosen.Count);
    }

    [Fact]
    public void ReadIndex_reports_correct_key_for_v3()
    {
        var (src, _) = MakeSampleTree();
        var archivePath = Path.Combine(_tmp, "out.rpa");
        _svc.Create(archivePath, src, RpaVersion.V3_0, key: 0x12345678, cancellationToken: Ct);
        var info = _svc.ReadIndex(archivePath);
        info.Key.Should().Be(0x12345678);
    }

    [Fact]
    public void ReadIndex_throws_on_non_rpa_file()
    {
        var bogus = Path.Combine(_tmp, "not.rpa");
        File.WriteAllText(bogus, "das ist kein archiv");
        var act = () => _svc.ReadIndex(bogus);
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Sizes_are_reported_correctly()
    {
        var (src, files) = MakeSampleTree();
        var archivePath = Path.Combine(_tmp, "out.rpa");
        _svc.Create(archivePath, src, cancellationToken: Ct);
        var info = _svc.ReadIndex(archivePath);

        foreach (var entry in info.Entries)
            entry.Size.Should().Be(files[entry.Path].Length);
    }
}
