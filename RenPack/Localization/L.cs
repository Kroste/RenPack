namespace RenPack.Localization;

/// <summary>
/// Kurzform-Zugriff auf lokalisierte Strings aus Code (ViewModels,
/// Services, Views-Code-behind). <c>L.T(key)</c> liest den Wert,
/// <c>L.F(key, args…)</c> formatiert mit <see cref="string.Format(string, object?[])"/>.
///
/// Im XAML dagegen wird <see cref="TrExtension"/> verwendet
/// (<c>{loc:Tr Key}</c>), damit die Bindings live umschalten.
/// </summary>
public static class L
{
    public static string T(string key) => LocalizationService.Instance[key];
    public static string F(string key, params object?[] args)
        => string.Format(LocalizationService.Instance[key], args);
}
