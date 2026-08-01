using System.Text.Json;
using NLog;

namespace RenPack.Services.Modding;

/// <summary>
/// End-to-End-Pipeline fuer den „Knopf-fuer-Dumme"-Mod-Workflow:
/// User waehlt den Spiel-Ordner (oder Spiel-Root, wir finden das
/// <c>game/</c> selbst), wir dekompilieren rekursiv alle .rpyc in ein
/// Temp-Verzeichnis, analysieren die .rpy, erzeugen den Mod, deployen
/// die modifizierten .rpy direkt neben die Original-.rpyc (Ren'Py bevorzugt
/// .rpy und regeneriert die .rpyc beim naechsten Start), sichern die
/// alte .rpyc als <c>.rpyc.krostemod-bak</c> und raeumen das Temp weg.
///
/// **Deploy-Layout** (verifiziert am Joker-WT-Mod fuer Rediscovering
/// Maria — <c>external/mods/</c>): Die modifizierte .rpy landet mit dem
/// selben Basisnamen wie die Original-.rpyc im selben Ordner. Ren'Py-
/// Loader-Prioritaet .rpy &gt; .rpyc verhindert Duplicate-Label-Errors,
/// weil das Original-Skript sonst nur als .rpyc vorliegt.
///
/// **Deinstallation**: Beim Deploy wird ein <c>KROSTEMOD_MANIFEST.json</c>
/// im <c>game/</c>-Ordner abgelegt, das jede angefasste Datei auflistet.
/// <see cref="Uninstall(string)"/> liest das Manifest, loescht die
/// modifizierten .rpy und ihre kompilierten Nachbarn (.rpyc), stellt die
/// gesicherten Originale wieder her und entfernt das Manifest.
/// </summary>
public sealed class OneClickModBuilder
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly RpycBatchService _batch;
    private readonly RenpyModAnalyzer _analyzer;
    private readonly KrosteWalkthroughGenerator _walkthrough;
    private readonly KrosteInfoScreenGenerator _infoScreen;

    public const string ManifestFileName = "KROSTEMOD_MANIFEST.json";
    public const string BackupSuffix = ".krostemod-bak";

    public OneClickModBuilder() : this(new RpycBatchService(), new RenpyModAnalyzer(),
        new KrosteWalkthroughGenerator(), new KrosteInfoScreenGenerator()) { }

    public OneClickModBuilder(RpycBatchService batch, RenpyModAnalyzer analyzer,
        KrosteWalkthroughGenerator walkthrough, KrosteInfoScreenGenerator infoScreen)
    {
        _batch = batch;
        _analyzer = analyzer;
        _walkthrough = walkthrough;
        _infoScreen = infoScreen;
    }

    /// <summary>Baut und deployt den Mod. <paramref name="userPickedFolder"/>
    /// darf sowohl der Spiel-Root sein als auch direkt der <c>game/</c>-Ordner
    /// — wir finden das <c>game/</c> automatisch.</summary>
    public OneClickResult Build(string userPickedFolder, ModTypeId modType,
        IProgress<OneClickProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(userPickedFolder))
            throw new DirectoryNotFoundException($"Ordner nicht gefunden: {userPickedFolder}");

        var gameDir = ResolveGameDir(userPickedFolder)
            ?? throw new InvalidOperationException(
                $"Kein Ren'Py-Spiel gefunden (weder .rpyc-Dateien in '{userPickedFolder}' noch ein 'game/'-Unterordner).");

        Log.Info("OneClickMod: gameDir={dir}, type={type}", gameDir, modType);
        progress?.Report(new OneClickProgress(OneClickPhase.Scanning, 0, 0, ""));

        var rpycFiles = RenpyRpycService.FindRpycFiles(gameDir).ToList();
        if (rpycFiles.Count == 0)
            throw new InvalidOperationException($"Keine .rpyc-Dateien in '{gameDir}' gefunden.");

        // Temp-Ordner: OS-Standard-Temp + eindeutige Session-ID.
        var tempRoot = Path.Combine(Path.GetTempPath(),
            $"RenPack-Mod-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            // 1. Decompile: Struktur relativ zu gameDir in Temp spiegeln.
            for (int i = 0; i < rpycFiles.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var rpyc = rpycFiles[i];
                var rel = Path.GetRelativePath(gameDir, rpyc);
                var tempRpy = Path.Combine(tempRoot, Path.ChangeExtension(rel, ".rpy"));
                Directory.CreateDirectory(Path.GetDirectoryName(tempRpy)!);
                progress?.Report(new OneClickProgress(
                    OneClickPhase.Decompiling, i + 1, rpycFiles.Count, rel));
                try
                {
                    _batch.DecompileFile(rpyc, tempRpy);
                }
                catch (Exception ex)
                {
                    // Einzelne Files duerfen kaputt sein — Story-relevante decken wir
                    // meistens ab, UI-Screens fallen manchmal durch. Weitermachen.
                    Log.Warn(ex, "Decompile fehlgeschlagen (weiter): {file}", rel);
                }
            }

            // 2. Analyze
            ct.ThrowIfCancellationRequested();
            progress?.Report(new OneClickProgress(OneClickPhase.Analyzing, 0, 0, ""));
            var analysis = _analyzer.Analyze(tempRoot);

            // 3. Mod bauen — in Temp-Sub-Ordner „mod/", damit generierter
            //    Output klar vom Decompile-Output getrennt liegt.
            ct.ThrowIfCancellationRequested();
            progress?.Report(new OneClickProgress(OneClickPhase.Generating, 0, 0, ""));
            var modOut = Path.Combine(tempRoot, "mod");
            int _ = modType switch
            {
                ModTypeId.Walkthrough => _walkthrough.Generate(tempRoot, modOut, analysis),
                _ => throw new NotSupportedException($"Mod-Typ noch nicht implementiert: {modType}"),
            };

            // 3b. F10-Info-Screen daneben legen — bringt Live-Variable-Werte
            // + Consumer-Liste ingame. Ergaenzt den Walkthrough um „warum-
            // Kontext" fuer den Spieler.
            _infoScreen.Generate(modOut, analysis);

            // 4. Deploy: modifizierte .rpy nach gameDir, .rpyc backuppen.
            ct.ThrowIfCancellationRequested();
            var deployed = Deploy(gameDir, modOut, modType, progress, ct);

            return new OneClickResult(gameDir, deployed.Count, analysis, deployed);
        }
        finally
        {
            progress?.Report(new OneClickProgress(OneClickPhase.Cleaning, 0, 0, ""));
            SafeDeleteTemp(tempRoot);
        }
    }

    /// <summary>Deployt die generierten .rpy aus <paramref name="modOutRoot"/>
    /// nach <paramref name="gameDir"/> (relativer Pfad wird beibehalten).
    /// Fuer jede ueberschriebene Datei wird die zugehoerige .rpyc als
    /// <c>.rpyc.krostemod-bak</c> gesichert (nur beim ersten Mal — laeuft
    /// der User den Mod ein zweites Mal, bleibt das erste Backup erhalten).
    /// Schreibt am Ende das Manifest fuer die Uninstall-Funktion.</summary>
    private List<DeployedFile> Deploy(string gameDir, string modOutRoot, ModTypeId modType,
        IProgress<OneClickProgress>? progress, CancellationToken ct)
    {
        var rpyFiles = Directory.Exists(modOutRoot)
            ? Directory.EnumerateFiles(modOutRoot, "*.rpy", SearchOption.AllDirectories).ToList()
            : [];

        var deployed = new List<DeployedFile>();
        for (int i = 0; i < rpyFiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var srcRpy = rpyFiles[i];
            var rel = Path.GetRelativePath(modOutRoot, srcRpy);
            var dstRpy = Path.Combine(gameDir, rel);
            var dstRpyc = Path.ChangeExtension(dstRpy, ".rpyc");
            var backup = dstRpyc + BackupSuffix;

            progress?.Report(new OneClickProgress(
                OneClickPhase.Deploying, i + 1, rpyFiles.Count, rel));

            Directory.CreateDirectory(Path.GetDirectoryName(dstRpy)!);

            // Backup: nur beim ersten Mal, damit das Original erhalten bleibt.
            bool backupCreated = false;
            if (File.Exists(dstRpyc) && !File.Exists(backup))
            {
                File.Move(dstRpyc, backup);
                backupCreated = true;
            }

            // Wenn schon eine .rpy vom User daneben lag (statt nur .rpyc),
            // ueberschreiben wir sie — aber merken uns dass sie da war,
            // damit Uninstall sie nicht loescht.
            bool preexistingRpy = File.Exists(dstRpy);
            File.Copy(srcRpy, dstRpy, overwrite: true);

            deployed.Add(new DeployedFile(rel, backupCreated, preexistingRpy));
        }

        // Mod-README daneben ins game/-Verzeichnis kopieren wenn vom
        // Generator erzeugt (Walkthrough legt eins in modOutRoot ab).
        var readme = Path.Combine(modOutRoot, "KROSTEMOD_README.md");
        if (File.Exists(readme))
            File.Copy(readme, Path.Combine(gameDir, "KROSTEMOD_README.md"), overwrite: true);

        // Asset-Files (non-.rpy) direkt aus modOut/ ins gameDir/ kopieren —
        // aktuell nur das Hint-Icon (krostemod_hint.png). Wir markieren die
        // als DeployedFile mit .png-Suffix, damit Uninstall sie sauber loescht.
        foreach (var asset in Directory.EnumerateFiles(modOutRoot, "*.png", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(modOutRoot, asset);
            var dst = Path.Combine(gameDir, rel);
            bool preexisting = File.Exists(dst);
            File.Copy(asset, dst, overwrite: true);
            deployed.Add(new DeployedFile(rel, BackupCreated: false, PreexistingRpy: preexisting));
        }

        // Manifest schreiben — die Basis fuer Uninstall.
        var manifest = new ModManifest(modType.ToString(), DateTime.UtcNow, deployed);
        var manifestPath = Path.Combine(gameDir, ManifestFileName);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest,
            new JsonSerializerOptions { WriteIndented = true }));

        Log.Info("Deploy fertig: {count} .rpy nach {dir}, Manifest={manifest}",
            deployed.Count, gameDir, manifestPath);
        return deployed;
    }

    /// <summary>Liest ein Manifest, macht das Deploy rueckgaengig: modifi-
    /// zierte .rpy loeschen, .rpyc aus dem Backup wiederherstellen. Ren'Py
    /// kann eventuell die .rpy schon zu einer neuen .rpyc kompiliert haben
    /// — die loeschen wir ebenfalls (die Backup-.rpyc ist die Wahrheit).</summary>
    public UninstallResult Uninstall(string gameDir)
    {
        var manifestPath = Path.Combine(gameDir, ManifestFileName);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"Kein Mod-Manifest gefunden in '{gameDir}'.");

        var manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("Manifest ist leer oder ungueltig.");

        int removed = 0, restored = 0;
        foreach (var entry in manifest.Files)
        {
            var dstRpy = Path.Combine(gameDir, entry.RelativePath);
            var dstRpyc = Path.ChangeExtension(dstRpy, ".rpyc");
            var backup = dstRpyc + BackupSuffix;

            // Wir loeschen die .rpy nur wenn sie NICHT vor dem Mod schon
            // da war (User-eigene Files bleiben erhalten).
            if (!entry.PreexistingRpy && File.Exists(dstRpy))
            {
                File.Delete(dstRpy);
                removed++;
            }

            // Nachkompilierte .rpyc weg — sonst wuerde Ren'Py sie beim
            // naechsten Start bevorzugen, obwohl das Backup korrekt ist.
            if (entry.BackupCreated && File.Exists(dstRpyc))
                File.Delete(dstRpyc);

            if (entry.BackupCreated && File.Exists(backup))
            {
                File.Move(backup, dstRpyc);
                restored++;
            }
        }

        File.Delete(manifestPath);
        var readme = Path.Combine(gameDir, "KROSTEMOD_README.md");
        if (File.Exists(readme)) File.Delete(readme);

        Log.Info("Uninstall: {removed} .rpy geloescht, {restored} .rpyc wiederhergestellt in {dir}",
            removed, restored, gameDir);
        return new UninstallResult(removed, restored);
    }

    /// <summary>Prueft, ob im angegebenen Ordner (oder seinem <c>game/</c>-
    /// Unterordner) ein KrosteMod-Manifest vorhanden ist.</summary>
    public string? FindInstalledManifest(string userPickedFolder)
    {
        var gameDir = ResolveGameDir(userPickedFolder);
        if (gameDir is null) return null;
        var manifestPath = Path.Combine(gameDir, ManifestFileName);
        return File.Exists(manifestPath) ? manifestPath : null;
    }

    /// <summary>Findet das <c>game/</c>-Verzeichnis: bevorzugt einen
    /// <c>game/</c>-Unterordner (Standard-Ren'Py-Release-Layout), sonst
    /// wird der Pick selbst genommen wenn er .rpyc enthaelt. Reihenfolge
    /// ist wichtig — hat der Root ein game/ UND rekursiv .rpyc, ist game/
    /// die richtige Wurzel (sonst wuerden Deploys ins Root landen).</summary>
    public static string? ResolveGameDir(string pickedFolder)
    {
        if (!Directory.Exists(pickedFolder)) return null;

        // Fall A: Standard-Ren'Py-Layout — es gibt einen 'game/'-Unterordner.
        var gameSub = Path.Combine(pickedFolder, "game");
        if (Directory.Exists(gameSub) &&
            Directory.EnumerateFiles(gameSub, "*.rpyc", SearchOption.AllDirectories).Any())
            return gameSub;

        // Fall B: der Pick ist schon das game/-Verzeichnis (enthaelt .rpyc).
        if (Directory.EnumerateFiles(pickedFolder, "*.rpyc", SearchOption.AllDirectories).Any())
            return pickedFolder;

        return null;
    }

    private static void SafeDeleteTemp(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { Log.Warn(ex, "Temp-Cleanup fehlgeschlagen: {dir}", dir); }
    }
}

public enum OneClickPhase { Scanning, Decompiling, Analyzing, Generating, Deploying, Cleaning }

public sealed record OneClickProgress(OneClickPhase Phase, int Done, int Total, string CurrentFile);

public sealed record OneClickResult(
    string GameDir, int DeployedFileCount, ModAnalysis Analysis,
    IReadOnlyList<DeployedFile> Deployed);

public sealed record DeployedFile(string RelativePath, bool BackupCreated, bool PreexistingRpy);

public sealed record UninstallResult(int RemovedFiles, int RestoredBackups);

/// <summary>Persistiertes Manifest im <c>game/</c>-Ordner, damit
/// <see cref="OneClickModBuilder.Uninstall"/> genau die selben Dateien
/// wieder anfassen kann.</summary>
public sealed record ModManifest(
    string ModType, DateTime CreatedUtc, IReadOnlyList<DeployedFile> Files);
