using System.IO.Pipes;
using NLog;

namespace RenPack.Services;

/// <summary>
/// Verhindert dass RenPack zwei Instanzen gleichzeitig laufen laesst.
/// Zweitstart holt die existierende Instance in den Vordergrund
/// (via <see cref="ActivationRequested"/>) und beendet sich selbst.
///
/// Umsetzung ueber Named Pipes (cross-platform: Windows-Named-Pipe,
/// Linux/macOS Unix-Domain-Socket unter <c>/tmp/CoreFxPipe_&lt;name&gt;</c>).
/// Pipe-Name enthaelt den Benutzernamen, damit verschiedene Nutzer
/// auf einem Terminalserver einander nicht blockieren.
///
/// Reihenfolge im <c>Program.Main</c> — vor Avalonia:
/// <code>
/// var guard = new SingleInstanceGuard();
/// if (!guard.TryClaim())
/// {
///     guard.NotifyPrimary();
///     guard.Dispose();
///     return;
/// }
/// App.PendingGuard = guard;
/// BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
/// </code>
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private const byte ActivateSignal = (byte)'A';

    private readonly string _pipeName =
        $"RenPack.SingleInstance.{Environment.UserName}";

    private NamedPipeServerStream? _server;
    private CancellationTokenSource? _cts;

    /// <summary>Wird gefeuert, wenn ein Zweitstart uns aktivieren moechte.
    /// Kommt auf einem ThreadPool-Thread — den UI-Thread selbst
    /// disptachen (<c>Dispatcher.UIThread.Post</c>).</summary>
    public event Action? ActivationRequested;

    /// <summary>Versucht die primaere Instanz zu werden. Liefert
    /// <c>false</c>, wenn schon eine andere Instanz laeuft — dann
    /// <see cref="NotifyPrimary"/> aufrufen und die App verlassen.</summary>
    public bool TryClaim()
    {
        try
        {
            _server = CreateServer();
            _cts = new CancellationTokenSource();
            _ = ListenAsync(_cts.Token);
            Log.Info("Single-Instance-Guard: primaere Instanz auf {pipe}", _pipeName);
            return true;
        }
        catch (IOException)
        {
            // Named Pipe / Socket schon in Benutzung. Auf Linux/macOS
            // kann das ein verwaister Socket-Datei sein — einen Recovery-
            // Versuch anhaengen.
            if (TryRecoverStaleSocket())
            {
                try
                {
                    _server = CreateServer();
                    _cts = new CancellationTokenSource();
                    _ = ListenAsync(_cts.Token);
                    Log.Info("Single-Instance-Guard: primaere Instanz (nach Stale-Socket-Recovery)");
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "Single-Instance-Guard: auch nach Recovery nicht claim-bar");
                    return false;
                }
            }
            return false;
        }
    }

    /// <summary>Signalisiert der existierenden Instanz, dass sie sich
    /// aktivieren soll. Kein Blockieren, kein Fehler bei Timeout.</summary>
    public void NotifyPrimary()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect(TimeSpan.FromMilliseconds(500));
            client.WriteByte(ActivateSignal);
            client.Flush();
            Log.Info("Single-Instance-Guard: primaere Instanz benachrichtigt");
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Single-Instance-Guard: Benachrichtigung fehlgeschlagen");
        }
    }

    private NamedPipeServerStream CreateServer() => new(
        _pipeName,
        PipeDirection.In,
        maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _server is not null)
        {
            try
            {
                await _server.WaitForConnectionAsync(ct);
                var buffer = new byte[1];
                var read = await _server.ReadAsync(buffer.AsMemory(0, 1), ct);
                if (read == 1 && buffer[0] == ActivateSignal)
                {
                    Log.Info("Single-Instance-Guard: Aktivierungs-Signal empfangen");
                    ActivationRequested?.Invoke();
                }
                _server.Disconnect();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Warn(ex, "Single-Instance-Guard: Listen-Iteration abgebrochen");
                await Task.Delay(500, ct);
            }
        }
    }

    /// <summary>Linux/macOS: veraltete Socket-Datei aufraeumen, wenn kein
    /// Prozess mehr darauf hoert. Auf Windows kein Effekt (OS raeumt
    /// Named Pipes selbst).</summary>
    private bool TryRecoverStaleSocket()
    {
        if (OperatingSystem.IsWindows()) return false;
        var socketPath = Path.Combine("/tmp", $"CoreFxPipe_{_pipeName}");
        try
        {
            using var probe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            probe.Connect(TimeSpan.FromMilliseconds(100));
            return false; // erreichbar → echte laufende Instanz
        }
        catch
        {
            try
            {
                if (File.Exists(socketPath))
                {
                    File.Delete(socketPath);
                    Log.Info("Single-Instance-Guard: veraltete Socket-Datei entfernt: {path}", socketPath);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Single-Instance-Guard: Socket-Aufraeumen fehlgeschlagen");
            }
            return false;
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _server?.Dispose(); } catch { }
        _server = null;
        _cts = null;
    }
}
