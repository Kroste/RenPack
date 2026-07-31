using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace RenPack.Views;

/// <summary>
/// Custom-Chrome nach Avalonia-12-Konvention (Kroste-Standard, Referenz: Amtsschimmel):
/// BorderOnly (NICHT None — sonst fehlen die nativen Resize-Griffe) und Client-Area
/// bis in die Dekoration ausgedehnt. Ohne ExtendClientArea liegt die OS-Caption-
/// Hit-Test-Zone über der eigenen Titelleiste und schluckt Klicks und Drag!
/// Lädt zusätzlich das App-Icon (ohne Icon trotzdem lauffähig).
/// </summary>
public class ChromeWindow : Window
{
    protected ChromeWindow()
    {
        WindowDecorations = WindowDecorations.BorderOnly;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = -1;
        CanResize = true;

        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://RenPack/Assets/RenPack.png"));
            Icon = new WindowIcon(stream);
        }
        catch
        {
            // Ohne Icon läuft die App trotzdem.
        }
    }
}
