using System.Runtime.InteropServices;
using NLog;
using RenPack.Services;
using RenPack.Services.Modding;

namespace RenPack.Cli;

/// <summary>
/// Command-Line-Interface fuer RenPack. Aktiviert, wenn das erste
/// Argument einer der Sub-Commands ist (<c>extract</c>, <c>decompile</c>,
/// <c>diff</c>, <c>help</c>, <c>--help</c>, <c>-h</c>). Sonst startet
/// die GUI wie gewohnt.
///
/// Unter Windows ist RenPack als <c>WinExe</c> kompiliert (keine
/// Standard-Konsole). <see cref="TryAttachParentConsole"/> haengt die
/// aktuelle Parent-Console via <c>AttachConsole(-1)</c> an, damit
/// <c>Console.WriteLine</c> im aufrufenden Terminal landet.
/// </summary>
public static class CliRunner
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public static bool IsCliInvocation(string[] args) =>
        args.Length > 0 && args[0] switch
        {
            "extract" or "decompile" or "diff" or "mod" or "help"
            or "-h" or "--help" or "-v" or "--version" => true,
            _ => false,
        };

    /// <summary>Fuehrt den CLI-Command aus und liefert den Exit-Code.
    /// Muss vor Avalonia gestartet werden.</summary>
    public static int Run(string[] args)
    {
        TryAttachParentConsole();
        try
        {
            return args[0] switch
            {
                "extract" => RunExtract(args[1..]),
                "decompile" => RunDecompile(args[1..]),
                "diff" => RunDiff(args[1..]),
                "mod" => RunMod(args[1..]),
                "-v" or "--version" => PrintVersion(),
                _ => PrintHelp(exitCode: 0),
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CLI-Fehler");
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    // ---- extract -----------------------------------------------------------

    private static int RunExtract(string[] args)
    {
        if (args.Length == 0) return Bail("extract needs <archive.rpa> [--dest <dir>]");
        string archive = args[0];
        string dest = GetOption(args, "--dest") ?? Path.GetFileNameWithoutExtension(archive);
        if (!File.Exists(archive)) return Bail($"archive not found: {archive}");

        var svc = new RenpyArchiveService();
        var info = svc.ReadIndex(archive);
        Console.WriteLine($"Reading {info.Version.ToDisplay()} · {info.Entries.Count} entries");

        int last = -1;
        var progress = new Progress<RpaProgress>(p =>
        {
            int pct = (int)(p.Fraction * 100);
            if (pct != last)
            {
                Console.Write($"\r  extracting {p.Current}/{p.Total} ({pct}%)      ");
                last = pct;
            }
        });
        int done = svc.Extract(info, info.Entries, dest, progress);
        Console.WriteLine($"\nExtracted {done} file(s) to {Path.GetFullPath(dest)}");
        return 0;
    }

    // ---- decompile ---------------------------------------------------------

    private static int RunDecompile(string[] args)
    {
        if (args.Length == 0) return Bail("decompile needs <path> (file or folder) [--skip-current]");
        string target = args[0];
        bool skipCurrent = args.Contains("--skip-current", StringComparer.OrdinalIgnoreCase);

        var batch = new RpycBatchService();
        if (Directory.Exists(target))
        {
            var progress = new Progress<(int done, int total, string current)>(p =>
                Console.Write($"\r  decompiling {p.done}/{p.total}: {Path.GetFileName(p.current)}      "));
            var result = batch.DecompileDirectory(target, progress, skipCurrent);
            Console.WriteLine($"\nDone: {result.Succeeded}/{result.Total} decompiled, "
                + $"{result.Failed} failed, {result.Skipped} skipped");
            foreach (var (file, err) in result.Errors.Take(10))
                Console.Error.WriteLine($"  {Path.GetFileName(file)}: {err}");
            return result.Failed == 0 ? 0 : 2;
        }
        if (File.Exists(target))
        {
            var outPath = batch.DecompileFile(target);
            Console.WriteLine($"Decompiled → {outPath}");
            return 0;
        }
        return Bail($"path not found: {target}");
    }

    // ---- diff --------------------------------------------------------------

    private static int RunDiff(string[] args)
    {
        if (args.Length < 2) return Bail("diff needs <a> <b> (both .save or both .rpa)");
        string a = args[0], b = args[1];
        if (!File.Exists(a) || !File.Exists(b)) return Bail("both files must exist");

        string ext = Path.GetExtension(a).ToLowerInvariant();
        if (ext == ".save") return DiffSaves(a, b);
        if (ext == ".rpa") return DiffArchives(a, b);
        return Bail($"unsupported extension: {ext} (expected .save or .rpa)");
    }

    private static int DiffSaves(string a, string b)
    {
        var svc = new RenpySaveService();
        var left = svc.Read(a);
        var right = svc.Read(b);
        var leftMap = left.Variables.ToDictionary(v => v.Name, v => v);
        var rightMap = right.Variables.ToDictionary(v => v.Name, v => v);
        int added = 0, removed = 0, modified = 0;
        foreach (var name in leftMap.Keys.Union(rightMap.Keys).OrderBy(n => n, StringComparer.Ordinal))
        {
            leftMap.TryGetValue(name, out var lv);
            rightMap.TryGetValue(name, out var rv);
            if (lv is null) { Console.WriteLine($"+ {name} = {rv!.Value}"); added++; continue; }
            if (rv is null) { Console.WriteLine($"− {name}"); removed++; continue; }
            if (lv.Value != rv.Value) { Console.WriteLine($"≠ {name}: {lv.Value} → {rv.Value}"); modified++; }
        }
        Console.WriteLine($"\n+ {added} added · − {removed} removed · ≠ {modified} modified");
        return 0;
    }

    private static int DiffArchives(string a, string b)
    {
        var svc = new RenpyArchiveService();
        var left = svc.ReadIndex(a);
        var right = svc.ReadIndex(b);
        var leftMap = left.Entries.ToDictionary(e => e.Path, e => e);
        var rightMap = right.Entries.ToDictionary(e => e.Path, e => e);
        int added = 0, removed = 0, modified = 0;
        foreach (var path in leftMap.Keys.Union(rightMap.Keys).OrderBy(n => n, StringComparer.Ordinal))
        {
            leftMap.TryGetValue(path, out var le);
            rightMap.TryGetValue(path, out var re);
            if (le is null) { Console.WriteLine($"+ {path} ({re!.Size} B)"); added++; continue; }
            if (re is null) { Console.WriteLine($"− {path}"); removed++; continue; }
            if (le.Size != re.Size) { Console.WriteLine($"≠ {path}: {le.Size} → {re.Size} B"); modified++; }
        }
        Console.WriteLine($"\n+ {added} added · − {removed} removed · ≠ {modified} modified");
        return 0;
    }

    // ---- mod ---------------------------------------------------------------

    private static int RunMod(string[] args)
    {
        if (args.Length == 0) return Bail("mod needs a sub-command (install/uninstall/walkthrough/analyze)");
        return args[0] switch
        {
            "install" => RunModInstall(args[1..]),
            "uninstall" => RunModUninstall(args[1..]),
            "walkthrough" => RunModWalkthrough(args[1..]),
            "analyze" => RunModAnalyze(args[1..]),
            _ => Bail($"unknown mod sub-command: {args[0]}"),
        };
    }

    private static int RunModInstall(string[] args)
    {
        if (args.Length == 0) return Bail("mod install needs <game-folder> [--type walkthrough]");
        string picked = args[0];
        string typeArg = GetOption(args, "--type") ?? "walkthrough";
        if (!Enum.TryParse<ModTypeId>(typeArg, ignoreCase: true, out var modType))
            return Bail($"unknown mod type: {typeArg}");
        if (!Directory.Exists(picked)) return Bail($"folder not found: {picked}");

        var builder = new OneClickModBuilder();
        var progress = new Progress<OneClickProgress>(p =>
            Console.WriteLine($"[{p.Phase}] {(p.Total > 0 ? $"{p.Done}/{p.Total} " : "")}{p.CurrentFile}"));
        var result = builder.Build(picked, modType, progress);
        Console.WriteLine();
        Console.WriteLine($"Installed {result.DeployedFileCount} .rpy file(s) into {result.GameDir}");
        Console.WriteLine($"Annotated {result.Analysis.Choices.Count} choice(s) across {result.Analysis.AnalyzedFiles.Count} file(s).");
        Console.WriteLine("Use 'renpack mod uninstall <game-folder>' to revert.");
        return 0;
    }

    private static int RunModUninstall(string[] args)
    {
        if (args.Length == 0) return Bail("mod uninstall needs <game-folder>");
        string picked = args[0];
        if (!Directory.Exists(picked)) return Bail($"folder not found: {picked}");
        var gameDir = OneClickModBuilder.ResolveGameDir(picked);
        if (gameDir is null) return Bail($"no Ren'Py game found under {picked}");
        var builder = new OneClickModBuilder();
        var result = builder.Uninstall(gameDir);
        Console.WriteLine($"Removed {result.RemovedFiles} mod file(s), restored {result.RestoredBackups} backup(s).");
        return 0;
    }

    private static int RunModWalkthrough(string[] args)
    {
        if (args.Length == 0) return Bail("mod walkthrough needs <source-dir> [--dest <dir>]");
        string src = args[0];
        string dest = GetOption(args, "--dest") ?? Path.Combine(src, "KrosteMod-Walkthrough");
        if (!Directory.Exists(src)) return Bail($"source folder not found: {src}");

        var analysis = new RenpyModAnalyzer().Analyze(src);
        Console.WriteLine($"Analyzed {analysis.AnalyzedFiles.Count} .rpy file(s): "
            + $"{analysis.Choices.Count} choices, {analysis.StoreVariables.Count} store vars, "
            + $"{analysis.Characters.Count} characters");
        if (analysis.Choices.Count == 0)
        {
            Console.WriteLine("No menu choices found — nothing to annotate.");
            return 0;
        }
        var gen = new KrosteWalkthroughGenerator();
        int written = gen.Generate(src, dest, analysis);
        Console.WriteLine($"Wrote {written} patched .rpy file(s) to {Path.GetFullPath(dest)}");
        Console.WriteLine("See KROSTEMOD_README.md in the output for installation instructions.");
        return 0;
    }

    private static int RunModAnalyze(string[] args)
    {
        if (args.Length == 0) return Bail("mod analyze needs <source-dir>");
        string src = args[0];
        if (!Directory.Exists(src)) return Bail($"source folder not found: {src}");

        var analysis = new RenpyModAnalyzer().Analyze(src);
        Console.WriteLine($"Files:      {analysis.AnalyzedFiles.Count}");
        Console.WriteLine($"Choices:    {analysis.Choices.Count}");
        Console.WriteLine($"Store vars: {analysis.StoreVariables.Count}");
        Console.WriteLine($"Characters: {analysis.Characters.Count}");
        Console.WriteLine();
        Console.WriteLine("Top store variables (by numeric-delta occurrence):");
        var topVars = analysis.Choices.SelectMany(c => c.Deltas)
            .Where(d => d.Op is "+=" or "-=")
            .GroupBy(d => d.Variable)
            .OrderByDescending(g => g.Count())
            .Take(10);
        foreach (var g in topVars) Console.WriteLine($"  {g.Key}: {g.Count()} changes");
        return 0;
    }

    // ---- helpers -----------------------------------------------------------

    private static int PrintVersion()
    {
        var ver = typeof(CliRunner).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        Console.WriteLine($"RenPack {ver}");
        return 0;
    }

    private static int PrintHelp(int exitCode)
    {
        Console.WriteLine("""
            RenPack — Ren'Py archive & save toolkit

            Usage:
              renpack extract <archive.rpa> [--dest <dir>]
              renpack decompile <path>      [--skip-current]
                                             (file or folder; recursive for folders)
              renpack diff <a.save> <b.save>
              renpack diff <a.rpa>  <b.rpa>
              renpack mod install <game-folder> [--type walkthrough|cheat]
                                             (one-shot: decompile + analyse + generate + install into game/)
                                             (walkthrough = hint tags at every choice + F10 impact screen)
                                             (cheat       = F11 cheat menu for numeric/bool story stats)
              renpack mod uninstall <game-folder>
                                             (restores originals via the .krostemod manifest)
              renpack mod analyze <source-dir>
                                             (advanced: source = already-decompiled game folder)
              renpack mod walkthrough <source-dir> [--dest <dir>]
                                             (advanced: generate mod into <dir>, do not install)
              renpack --version
              renpack --help

            Without arguments RenPack starts the desktop UI.
            """);
        return exitCode;
    }

    private static int Bail(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        Console.Error.WriteLine("Run 'renpack --help' for usage.");
        return 2;
    }

    private static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    // ---- Windows-Console-Anhang -------------------------------------------

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    private static void TryAttachParentConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { AttachConsole(ATTACH_PARENT_PROCESS); }
        catch { /* best effort — no console = swallowed output, acceptable */ }
    }
}
