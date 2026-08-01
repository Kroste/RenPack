using NLog;

namespace RenPack.Services;

/// <summary>Batch-Verarbeitung für die Dekompilierung: einzelne oder ganze
/// Ordner voller .rpyc-Dateien werden nach .rpy neben der Original-Datei
/// (oder in einen Ziel-Ordner) geschrieben.</summary>
public sealed class RpycBatchService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly RenpyRpycService _reader;
    private readonly RenpyRpycDecompiler _writer;

    public RpycBatchService(RenpyRpycService reader, RenpyRpycDecompiler writer)
    {
        _reader = reader;
        _writer = writer;
    }

    public RpycBatchService() : this(new RenpyRpycService(), new RenpyRpycDecompiler()) { }

    /// <summary>Dekompiliert eine einzelne <c>.rpyc</c> und schreibt das Ergebnis
    /// neben die Datei (endet auf <c>.rpy</c>) oder — wenn
    /// <paramref name="destinationOverride"/> gesetzt ist — dorthin.</summary>
    public string DecompileFile(string rpycPath, string? destinationOverride = null)
    {
        var stmts = _reader.ReadAst(rpycPath);
        string content = _writer.Decompile(stmts);
        string target = destinationOverride ?? Path.ChangeExtension(rpycPath, ".rpy");
        File.WriteAllText(target, content);
        Log.Info("Dekompiliert: {src} → {dst} ({stmts} Statements)",
            rpycPath, target, stmts.Count);
        return target;
    }

    /// <summary>Findet rekursiv alle <c>.rpyc</c> unter <paramref name="rootDir"/>
    /// und dekompiliert sie. Fehler pro Datei werden geloggt, aber der Batch
    /// läuft weiter — am Ende gibt es eine <see cref="BatchResult"/>-Zusammenfassung.
    ///
    /// <paramref name="skipUpToDate"/>: wenn <c>true</c>, werden .rpyc
    /// uebersprungen, deren .rpy-Nachbardatei existiert und juenger ist —
    /// Re-Runs werden dadurch von Minuten auf Sekunden verkuerzt.</summary>
    public BatchResult DecompileDirectory(string rootDir,
        IProgress<(int done, int total, string current)>? progress = null,
        bool skipUpToDate = false,
        CancellationToken cancellationToken = default)
    {
        var files = RenpyRpycService.FindRpycFiles(rootDir).ToList();
        Log.Info("Batch-Dekompilierung: {count} .rpyc-Dateien unter {root} (skipUpToDate={skip})",
            files.Count, rootDir, skipUpToDate);

        int ok = 0, failed = 0, skipped = 0;
        var errors = new List<(string file, string error)>();
        for (int i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var f = files[i];
            if (skipUpToDate && IsUpToDate(f))
            {
                skipped++;
                progress?.Report((i + 1, files.Count, f));
                continue;
            }
            try
            {
                DecompileFile(f);
                ok++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add((f, ex.Message));
                Log.Warn(ex, "Dekompilieren fehlgeschlagen: {file}", f);
            }
            progress?.Report((i + 1, files.Count, f));
        }
        return new BatchResult(files.Count, ok, failed, skipped, errors);
    }

    /// <summary>True, wenn die .rpy neben der .rpyc existiert UND juenger
    /// (oder gleich alt) ist. Dann macht ein Redecompile keinen Sinn.</summary>
    private static bool IsUpToDate(string rpycPath)
    {
        var rpyPath = Path.ChangeExtension(rpycPath, ".rpy");
        if (!File.Exists(rpyPath)) return false;
        var rpycTime = File.GetLastWriteTimeUtc(rpycPath);
        var rpyTime = File.GetLastWriteTimeUtc(rpyPath);
        return rpyTime >= rpycTime;
    }
}

public sealed record BatchResult(
    int Total,
    int Succeeded,
    int Failed,
    int Skipped,
    IReadOnlyList<(string File, string Error)> Errors);
