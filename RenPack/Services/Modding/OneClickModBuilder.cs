using System.Text.Json;
using NLog;
using RenPack.Services;

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
    private readonly KrosteCheatGenerator _cheat;
    private readonly KrosteRenameGenerator _rename;
    private readonly IRenpyArchiveService _archive;

    public const string ManifestFileName = "KROSTEMOD_MANIFEST.json";
    public const string BackupSuffix = ".krostemod-bak";

    public OneClickModBuilder() : this(new RpycBatchService(), new RenpyModAnalyzer(),
        new KrosteWalkthroughGenerator(), new KrosteInfoScreenGenerator(),
        new KrosteCheatGenerator(), new KrosteRenameGenerator(),
        new RenpyArchiveService()) { }

    public OneClickModBuilder(RpycBatchService batch, RenpyModAnalyzer analyzer,
        KrosteWalkthroughGenerator walkthrough, KrosteInfoScreenGenerator infoScreen,
        KrosteCheatGenerator cheat, KrosteRenameGenerator rename,
        IRenpyArchiveService archive)
    {
        _batch = batch;
        _analyzer = analyzer;
        _walkthrough = walkthrough;
        _infoScreen = infoScreen;
        _cheat = cheat;
        _rename = rename;
        _archive = archive;
    }

    /// <summary>Baut und deployt den Mod. <paramref name="userPickedFolder"/>
    /// darf sowohl der Spiel-Root sein als auch direkt der <c>game/</c>-Ordner
    /// — wir finden das <c>game/</c> automatisch.
    ///
    /// <paramref name="renameConfigProvider"/> wird NUR beim Rename-Mod-
    /// Typ aufgerufen, nach der Analyse (weil erst dann die Character-Liste
    /// bekannt ist). Der Provider bekommt die Character-Liste und liefert
    /// die Mappings zurueck. Gibt er <c>null</c> zurueck (User-Cancel),
    /// wird der Build abgebrochen.</summary>
    public OneClickResult Build(string userPickedFolder, ModTypeId modType,
        IProgress<OneClickProgress>? progress = null,
        CancellationToken ct = default,
        Func<IReadOnlyList<RpyCharacter>, IReadOnlyList<RpySayStatement>, RenameConfig?>? renameConfigProvider = null)
    {
        if (!Directory.Exists(userPickedFolder))
            throw new DirectoryNotFoundException($"Ordner nicht gefunden: {userPickedFolder}");

        var gameDir = ResolveGameDir(userPickedFolder)
            ?? throw new InvalidOperationException(
                $"Kein Ren'Py-Spiel gefunden (weder .rpyc-Dateien in '{userPickedFolder}' noch ein 'game/'-Unterordner).");

        Log.Info("OneClickMod: gameDir={dir}, type={type}", gameDir, modType);
        progress?.Report(new OneClickProgress(OneClickPhase.Scanning, 0, 0, ""));

        // Temp-Ordner-Struktur:
        //   tempRoot/                 — Session-Root
        //     extracted/              — nur wenn packedMode (.rpa-Extract)
        //     decompiled/             — unsere .rpy-Outputs (immer nur hier)
        //     mod/                    — Mod-Generator-Output
        // WICHTIG: extracted UND decompiled sind strikt getrennt, sonst
        // findet der Analyzer die Extract-.rpy (manche .rpa enthalten
        // sogar .rpy!) UND unsere Decompile-.rpy als Duplikate — der
        // Walkthrough patcht dann beide und der Deploy schleppt den
        // `extracted/`-Prefix ins gameDir → Ren'Py meldet Duplicate-
        // Labels an `game/script.rpy` UND `game/extracted/script.rpy`.
        // Verifiziert an Interview Desires 0.23 (v0.12.1-Bug).
        var tempRoot = Path.Combine(Path.GetTempPath(),
            $"RenPack-Mod-{Guid.NewGuid():N}");
        var decompiledDir = Path.Combine(tempRoot, "decompiled");
        Directory.CreateDirectory(decompiledDir);

        // Source-Ordner fuer .rpyc: default gameDir. Wenn dort keine .rpyc
        // liegen, aber .rpa-Archive vorhanden sind (z.B. Interview Desires
        // oder viele Steam-Distributionen), extrahieren wir sie ins Temp
        // und arbeiten mit dem Extract als virtuellem game/. Original-.rpa
        // bleibt unangetastet. packedMode signalisiert dem Deploy dass die
        // Original-.rpyc NICHT im Filesystem liegen → kein Backup noetig.
        string rpycSource = gameDir;
        bool packedMode = false;
        var rpycFiles = RenpyRpycService.FindRpycFiles(gameDir).ToList();
        if (rpycFiles.Count == 0)
        {
            var rpas = Directory.EnumerateFiles(gameDir, "*.rpa", SearchOption.TopDirectoryOnly)
                .ToList();
            if (rpas.Count == 0)
                throw new InvalidOperationException(
                    $"Weder .rpyc-Dateien noch .rpa-Archive in '{gameDir}' gefunden.");

            var extractedDir = Path.Combine(tempRoot, "extracted");
            Directory.CreateDirectory(extractedDir);
            Log.Info("Kein .rpyc im gameDir — extrahiere {n} .rpa → {temp}",
                rpas.Count, extractedDir);
            foreach (var rpa in rpas)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new OneClickProgress(
                    OneClickPhase.Scanning, 0, 0, $"extract {Path.GetFileName(rpa)}"));
                var info = _archive.ReadIndex(rpa);
                _archive.ExtractAll(info, extractedDir, cancellationToken: ct);
            }
            rpycFiles = RenpyRpycService.FindRpycFiles(extractedDir).ToList();
            if (rpycFiles.Count == 0)
                throw new InvalidOperationException(
                    $"Auch nach Extrakt keine .rpyc gefunden in '{gameDir}'.");
            rpycSource = extractedDir;
            packedMode = true;
        }

        try
        {
            // 1. Decompile: alle .rpy ausschliesslich nach decompiledDir/
            //    (nicht in tempRoot direkt — sonst kollidiert mit extracted/).
            for (int i = 0; i < rpycFiles.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var rpyc = rpycFiles[i];
                var rel = Path.GetRelativePath(rpycSource, rpyc);
                var tempRpy = Path.Combine(decompiledDir, Path.ChangeExtension(rel, ".rpy"));
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

            // 2. Analyze — nur der decompiledDir, NICHT tempRoot!
            ct.ThrowIfCancellationRequested();
            progress?.Report(new OneClickProgress(OneClickPhase.Analyzing, 0, 0, ""));
            var analysis = _analyzer.Analyze(decompiledDir);

            // 3. Mod bauen — in Temp-Sub-Ordner „mod/", damit generierter
            //    Output klar vom Decompile-Output getrennt liegt.
            ct.ThrowIfCancellationRequested();
            progress?.Report(new OneClickProgress(OneClickPhase.Generating, 0, 0, ""));
            var modOut = Path.Combine(tempRoot, "mod");
            switch (modType)
            {
                case ModTypeId.Walkthrough:
                    _walkthrough.Generate(decompiledDir, modOut, analysis);
                    break;
                case ModTypeId.Cheat:
                    // Cheat-Mod hat kein .rpy-Patching noetig, nur die
                    // krostemod_cheat.rpy landet im modOut/.
                    Directory.CreateDirectory(modOut);
                    _cheat.Generate(modOut, analysis);
                    break;
                case ModTypeId.Rename:
                    // Rename braucht User-Input NACH der Analyse (Character-
                    // Liste muss erst da sein). Provider-Callback wird
                    // synchronous aufgerufen — der Aufrufer ist verantwortlich
                    // die Task-Ausfuehrung ggf. auf den UI-Thread zu
                    // dispatchen und den Dialog zu zeigen.
                    if (renameConfigProvider is null)
                        throw new InvalidOperationException(
                            "Rename-Mod-Typ erfordert einen renameConfigProvider.");
                    var renameConfig = renameConfigProvider(analysis.Characters, analysis.SayStatements)
                        ?? throw new OperationCanceledException(
                            "Rename-Konfiguration vom User abgebrochen.");
                    Directory.CreateDirectory(modOut);
                    // decompiledSourceRoot = decompiledDir damit der Generator
                    // die dekompilierten .rpy fuer Body-Text-Patches (E4b) hat.
                    _rename.Generate(modOut, analysis, renameConfig,
                        decompiledSourceRoot: decompiledDir);
                    break;
                default:
                    throw new NotSupportedException($"Mod-Typ noch nicht implementiert: {modType}");
            }

            // 3b. F10-Info-Screen daneben legen — bringt Live-Variable-Werte
            // + Consumer-Liste ingame. Ergaenzt den Walkthrough/Cheat um
            // „warum-Kontext" fuer den Spieler.
            _infoScreen.Generate(modOut, analysis);

            // 4. Deploy: modifizierte .rpy nach gameDir, .rpyc backuppen
            //    (nur wenn nicht packedMode — bei packed liegen Originale
            //    in der .rpa, da gibt's nichts zu backuppen).
            ct.ThrowIfCancellationRequested();
            var deployed = Deploy(gameDir, modOut, modType, packedMode, progress, ct);

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
        bool packedMode, IProgress<OneClickProgress>? progress, CancellationToken ct)
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

            // Backup: nur beim ersten Mal UND nur wenn wir im Filesystem-
            // Modus sind. Im packedMode (.rpa vorhanden, .rpyc gepackt)
            // liegt das Original in der .rpa — es gibt nichts im Filesystem
            // zu backuppen, und die .rpa selbst fassen wir nie an.
            bool backupCreated = false;
            if (!packedMode && File.Exists(dstRpyc) && !File.Exists(backup))
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
    /// die richtige Wurzel (sonst wuerden Deploys ins Root landen).
    ///
    /// Akzeptiert auch Ordner mit NUR .rpa (gepackte Distributionen — Steam,
    /// Interview Desires, viele andere). Der Build-Loop entpackt die dann
    /// automatisch ins Temp und arbeitet mit dem Extract.</summary>
    public static string? ResolveGameDir(string pickedFolder)
    {
        if (!Directory.Exists(pickedFolder)) return null;

        // Fall A: Standard-Ren'Py-Layout — es gibt einen 'game/'-Unterordner
        // mit entweder .rpyc oder .rpa (bei gepackten Spielen).
        var gameSub = Path.Combine(pickedFolder, "game");
        if (Directory.Exists(gameSub) && HasRenpyContent(gameSub))
            return gameSub;

        // Fall B: der Pick ist schon das game/-Verzeichnis.
        if (HasRenpyContent(pickedFolder))
            return pickedFolder;

        return null;
    }

    private static bool HasRenpyContent(string dir)
    {
        // .rpyc rekursiv (Filesystem-Content) ODER .rpa im Top-Level
        // (gepackte Distribution).
        return Directory.EnumerateFiles(dir, "*.rpyc", SearchOption.AllDirectories).Any()
            || Directory.EnumerateFiles(dir, "*.rpa", SearchOption.TopDirectoryOnly).Any();
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
