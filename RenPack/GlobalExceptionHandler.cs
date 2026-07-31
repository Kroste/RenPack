using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using NLog;
using RenPack.Views;

namespace RenPack;

/// <summary>
/// Kroste-Standard: unbehandelte Exceptions loggen und dem Nutzer zeigen, statt still
/// abzustürzen. Avalonia 12 hat kein zentrales Dispatcher-Exception-Event — UI-Fehler
/// laufen letztlich in AppDomain.UnhandledException, ergänzt um Task-Exceptions.
/// </summary>
public static class GlobalExceptionHandler
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static IClassicDesktopStyleApplicationLifetime? _lifetime;

    public static void Attach(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        _lifetime = lifetime;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Log.Fatal(ex, "Unbehandelte Ausnahme (AppDomain), Terminating={t}", e.IsTerminating);
            ShowError(ex);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unbeobachtete Task-Ausnahme");
            e.SetObserved();
            ShowError(e.Exception);
        };
    }

    private static void ShowError(Exception? ex)
    {
        var owner = _lifetime?.MainWindow;
        if (owner is null) return;
        try
        {
            Dispatcher.UIThread.Post(async void () =>
            {
                try
                {
                    await MessageBox.ShowAsync(owner, "Unerwarteter Fehler",
                        "Es ist ein unerwarteter Fehler aufgetreten. Details stehen im Log.\n\n" +
                        (ex?.Message ?? "Unbekannter Fehler"));
                }
                catch (Exception dialogEx)
                {
                    Log.Error(dialogEx, "Fehlerdialog konnte nicht angezeigt werden");
                }
            });
        }
        catch (Exception postEx)
        {
            Log.Error(postEx, "Fehlerbehandlung fehlgeschlagen");
        }
    }
}
