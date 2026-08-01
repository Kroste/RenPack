using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace RenPack.Localization;

/// <summary>
/// Singleton-Service für UI-Lokalisierung. Liefert übersetzte Strings aus
/// den <c>Strings.*.resx</c>-Ressourcen und benachrichtigt gebundene XAML-
/// Elemente per <see cref="INotifyPropertyChanged"/>, sobald die aktive
/// Sprache wechselt — damit funktioniert der Sprachwechsel live, ohne
/// App-Neustart.
///
/// **Benutzung im XAML:** über die <see cref="TrExtension"/>:
/// <code>Text="{loc:Tr OpenArchive}"</code>. Fallback (falls Key fehlt):
/// der Key wird sichtbar ausgegeben, damit fehlende Übersetzungen sofort
/// auffallen.
///
/// **Zentrale Design-Entscheidung:** wir generieren KEINE
/// <c>Strings.Designer.cs</c>-Wrapperklasse (der ResX-Generator läuft nur
/// unter Visual Studio zuverlässig). Stattdessen greift der Service direkt
/// per <see cref="ResourceManager"/> auf die eingebetteten Ressourcen zu —
/// funktioniert plattformübergreifend, tooling-unabhängig, `dotnet build`
/// reicht.
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    public static LocalizationService Instance { get; } = new();

    /// <summary>Vom App-Nutzer konfigurierbare Sprachen. Der ISO-Code passt
    /// zur <c>Strings.{iso}.resx</c>-Dateinamenskonvention. Neutral (=
    /// Fallback) ist Englisch — die englische <c>Strings.resx</c> liegt
    /// ohne Sprach-Suffix. <c>Flag</c> ist das Emoji-Fahnen-Piktogramm
    /// (Regional Indicator Symbols) für den Selektor. Englisch bekommt
    /// die UK-Flagge (🇬🇧) als international neutrales Symbol.</summary>
    public static IReadOnlyList<(string Iso, string Display, string Flag)> SupportedCultures { get; } = new[]
    {
        ("en", "English", "🇬🇧"),
        ("de", "Deutsch", "🇩🇪"),
        ("fr", "Français", "🇫🇷"),
        ("ru", "Русский", "🇷🇺"),
    };

    private readonly ResourceManager _rm = new(
        "RenPack.Localization.Strings",
        typeof(LocalizationService).Assembly);

    private CultureInfo _current = CultureInfo.CurrentUICulture;

    /// <summary>Aktuell aktive UI-Sprache. Bei Zuweisung feuert der
    /// Service <see cref="PropertyChanged"/> für den Indexer, wodurch alle
    /// aktiven <c>{loc:Tr}</c>-Bindings re-evaluiert werden.</summary>
    public CultureInfo Current
    {
        get => _current;
        set
        {
            if (Equals(_current, value)) return;
            _current = value;
            CultureInfo.CurrentUICulture = value;
            OnPropertyChanged("Item[]"); // WPF/Avalonia-Konvention für Indexer-Refresh
            OnPropertyChanged(nameof(Current));
            OnPropertyChanged(nameof(CurrentIso));
        }
    }

    public string CurrentIso => TwoLetterOrDefault(_current);

    /// <summary>Setzt die Sprache anhand ihres ISO-Codes ("en"/"de"/"fr"/"ru").
    /// Unbekannte Codes werden auf "en" (Neutral) gemappt.</summary>
    public void SetCulture(string iso)
    {
        Current = SupportedCultures.Any(c => c.Iso == iso)
            ? CultureInfo.GetCultureInfo(iso)
            : CultureInfo.InvariantCulture; // Neutral → englische Resx
    }

    /// <summary>Indexer für XAML-Binding. Fallback bei fehlendem Key: der
    /// Key selbst — dann fällt eine unlokalisierte Stelle sofort auf.</summary>
    public string this[string key]
    {
        get
        {
            try
            {
                return _rm.GetString(key, _current) ?? $"!{key}!";
            }
            catch (MissingManifestResourceException)
            {
                return $"!{key}!";
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string TwoLetterOrDefault(CultureInfo c)
    {
        var iso = c.TwoLetterISOLanguageName;
        return SupportedCultures.Any(x => x.Iso == iso) ? iso : "en";
    }
}
