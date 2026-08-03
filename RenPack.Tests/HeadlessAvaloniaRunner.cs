using Avalonia.Headless;

namespace RenPack.Tests;

/// <summary>
/// Shared Avalonia-Headless-Session fuer UI-Smoke-Tests. Ein einziger
/// dedizierter Dispatcher-Thread wird beim ersten Zugriff aufgesetzt und
/// bleibt fuer die gesamte Test-Assembly bestehen — Avalonia's App-Klasse
/// kann pro Prozess nur einmal initialisiert werden.
///
/// **Warum kein <c>Avalonia.Headless.XUnit</c>?** Das Package ist an
/// xunit v2 gebunden; unser Repo nutzt xunit.v3 mit anderen
/// Test-Framework-Signaturen. Statt einen v3-Adapter zu bauen, umgehen
/// wir die Attribute komplett und dispatchen den Test-Body per
/// <see cref="HeadlessUnitTestSession.Dispatch(Action, CancellationToken)"/>
/// selbst auf den UI-Thread.
///
/// **Usage:** In einem normalen <c>[Fact]</c> einfach den Test-Body
/// in <see cref="RunAsync"/> wrappen; die Session bleibt zwischen
/// Tests aktiv (Session-Reuse ist billiger als jedes Mal neu starten
/// und vermeidet Race-Conditions beim mehrfachen App-Init).
/// </summary>
internal static class HeadlessAvaloniaRunner
{
    private static readonly Lock _initLock = new();
    private static HeadlessUnitTestSession? _session;

    public static Task RunAsync(Action test, CancellationToken ct = default)
    {
        var s = EnsureSession();
        return s.Dispatch(test, ct);
    }

    public static Task RunAsync(Func<Task> test, CancellationToken ct = default)
    {
        var s = EnsureSession();
        return s.Dispatch(test, ct);
    }

    private static HeadlessUnitTestSession EnsureSession()
    {
        if (_session is not null) return _session;
        lock (_initLock)
        {
            _session ??= HeadlessUnitTestSession.StartNew(typeof(RenPack.App));
            return _session;
        }
    }
}
