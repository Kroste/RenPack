using System.Text.Json;
using FluentAssertions;
using RenPack.Services.Modding;
using Xunit;

namespace RenPack.Tests;

/// <summary>
/// Tests fuer den One-Click-Mod-Builder-Orchestrator. Der Decompile-Schritt
/// braucht echte .rpyc-Fixtures — die decken die bestehenden
/// <see cref="RpycDecompilerTests"/> ab. Hier fokussieren wir uns auf die
/// Orchestrator-Logik: <see cref="OneClickModBuilder.ResolveGameDir"/>,
/// Deploy-Layout via <see cref="OneClickModBuilder.Uninstall"/> Roundtrip,
/// und Manifest-Handling.
/// </summary>
public sealed class OneClickModBuilderTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(),
        $"renpack-oneclick-tests-{Guid.NewGuid():N}");

    public OneClickModBuilderTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    // ---- ResolveGameDir ---------------------------------------------------

    [Fact]
    public void ResolveGameDir_returns_folder_directly_when_it_contains_rpyc()
    {
        File.WriteAllBytes(Path.Combine(_tmp, "script.rpyc"), [1, 2, 3]);
        var resolved = OneClickModBuilder.ResolveGameDir(_tmp);
        resolved.Should().Be(_tmp);
    }

    [Fact]
    public void ResolveGameDir_returns_game_subfolder_when_root_has_no_rpyc()
    {
        var gameDir = Path.Combine(_tmp, "game");
        Directory.CreateDirectory(gameDir);
        File.WriteAllBytes(Path.Combine(gameDir, "script.rpyc"), [1, 2, 3]);
        // Root selbst hat keine .rpyc, aber game/ schon.
        var resolved = OneClickModBuilder.ResolveGameDir(_tmp);
        resolved.Should().Be(gameDir);
    }

    [Fact]
    public void ResolveGameDir_returns_null_when_no_rpyc_anywhere()
    {
        File.WriteAllText(Path.Combine(_tmp, "readme.txt"), "hi");
        var resolved = OneClickModBuilder.ResolveGameDir(_tmp);
        resolved.Should().BeNull();
    }

    [Fact]
    public void ResolveGameDir_finds_rpyc_in_deep_subdirectory()
    {
        var deep = Path.Combine(_tmp, "sub", "deeper");
        Directory.CreateDirectory(deep);
        File.WriteAllBytes(Path.Combine(deep, "x.rpyc"), [1]);
        var resolved = OneClickModBuilder.ResolveGameDir(_tmp);
        resolved.Should().Be(_tmp);
    }

    // ---- Uninstall via Manifest ------------------------------------------

    [Fact]
    public void Uninstall_restores_rpyc_backup_and_deletes_mod_rpy()
    {
        // Simulierter Zustand nach einem Build:
        //  script.rpy               — vom Mod erzeugt (soll weg)
        //  script.rpyc              — evtl. schon neu kompiliert (soll weg)
        //  script.rpyc.krostemod-bak — Original-Backup (soll → script.rpyc)
        //  KROSTEMOD_MANIFEST.json  — Liste der Deployments
        File.WriteAllText(Path.Combine(_tmp, "script.rpy"), "# mod-rpy");
        File.WriteAllBytes(Path.Combine(_tmp, "script.rpyc"), [9, 9, 9]); // recompiled
        File.WriteAllBytes(Path.Combine(_tmp, "script.rpyc.krostemod-bak"),
            [1, 2, 3, 4]); // original

        WriteManifest(_tmp,
            new DeployedFile("script.rpy", BackupCreated: true, PreexistingRpy: false));

        var builder = new OneClickModBuilder();
        var result = builder.Uninstall(_tmp);

        result.RemovedFiles.Should().Be(1);
        result.RestoredBackups.Should().Be(1);
        File.Exists(Path.Combine(_tmp, "script.rpy")).Should().BeFalse();
        File.Exists(Path.Combine(_tmp, "script.rpyc.krostemod-bak")).Should().BeFalse();
        File.ReadAllBytes(Path.Combine(_tmp, "script.rpyc"))
            .Should().Equal([1, 2, 3, 4]);
        File.Exists(Path.Combine(_tmp, OneClickModBuilder.ManifestFileName)).Should().BeFalse();
    }

    [Fact]
    public void Uninstall_keeps_preexisting_user_rpy_files()
    {
        // Der User hatte schon eine .rpy — Uninstall darf sie nicht loeschen.
        File.WriteAllText(Path.Combine(_tmp, "custom.rpy"), "# user file");
        File.WriteAllBytes(Path.Combine(_tmp, "custom.rpyc"), [9]); // recompiled

        // Kein Backup, weil vor dem Mod schon keine .rpyc da war (oder es war
        // eine .rpy und der Mod hat sie nur ueberschrieben).
        WriteManifest(_tmp,
            new DeployedFile("custom.rpy", BackupCreated: false, PreexistingRpy: true));

        var builder = new OneClickModBuilder();
        var result = builder.Uninstall(_tmp);

        result.RemovedFiles.Should().Be(0);
        result.RestoredBackups.Should().Be(0);
        File.Exists(Path.Combine(_tmp, "custom.rpy")).Should().BeTrue();
    }

    [Fact]
    public void Uninstall_throws_when_no_manifest_present()
    {
        var builder = new OneClickModBuilder();
        var act = () => builder.Uninstall(_tmp);
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void FindInstalledManifest_returns_null_when_no_game_dir_resolvable()
    {
        var builder = new OneClickModBuilder();
        builder.FindInstalledManifest(_tmp).Should().BeNull();
    }

    [Fact]
    public void FindInstalledManifest_finds_manifest_in_game_subfolder()
    {
        var gameDir = Path.Combine(_tmp, "game");
        Directory.CreateDirectory(gameDir);
        File.WriteAllBytes(Path.Combine(gameDir, "x.rpyc"), [1]);
        WriteManifest(gameDir); // manifest im aufgeloesten game/-Ordner

        var builder = new OneClickModBuilder();
        var path = builder.FindInstalledManifest(_tmp); // User-Pick auf Root
        path.Should().NotBeNull();
        path.Should().Be(Path.Combine(gameDir, OneClickModBuilder.ManifestFileName));
    }

    private static void WriteManifest(string dir, params DeployedFile[] files)
    {
        var manifest = new ModManifest("Walkthrough", DateTime.UtcNow, files);
        File.WriteAllText(Path.Combine(dir, OneClickModBuilder.ManifestFileName),
            JsonSerializer.Serialize(manifest));
    }
}
