using Avalonia;
using Avalonia.Media;
using NLog;
using RenPack.Logging;

namespace RenPack;

internal static class Program
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // Initialization code. Vor AvaloniaUI-Aufrufen darf nichts gestartet werden,
    // das SynchronizationContext braucht.
    [STAThread]
    public static void Main(string[] args)
    {
        MaskingLayoutRenderer.Register();
        Log.Info("RenPack startet (args: {args})", string.Join(' ', args));
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "RenPack mit unbehandeltem Fehler beendet");
            throw;
        }
        finally
        {
            Log.Info("RenPack beendet");
            LogManager.Shutdown();
        }
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        string emojiFont = OperatingSystem.IsWindows() ? "Segoe UI Emoji"
            : OperatingSystem.IsMacOS() ? "Apple Color Emoji"
            : "Noto Color Emoji";

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "fonts:Inter#Inter",
                FontFallbacks = [new FontFallback { FontFamily = new FontFamily(emojiFont) }],
            })
            .LogToTrace();
    }
}
