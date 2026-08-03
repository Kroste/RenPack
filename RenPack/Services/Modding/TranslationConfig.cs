namespace RenPack.Services.Modding;

/// <summary>Ren'Py's kanonische Sprach-Codes (lowercase englische Woerter,
/// wie sie in <c>define config.language = "german"</c> und <c>translate
/// german strings:</c> stehen). Nicht ISO — Ren'Py's eigene Konvention.</summary>
public enum TargetLanguage
{
    English, German, French, Spanish, Italian, Portuguese, Polish,
    Russian, Ukrainian, Czech,
    ChineseSimplified, Japanese, Korean,
    Turkish, Arabic,
}

public static class TargetLanguageMap
{
    /// <summary>Ren'Py-Language-Code (fuer <c>translate X strings:</c>-Block
    /// und <c>config.language</c>).</summary>
    public static string ToRenpyCode(this TargetLanguage lang) => lang switch
    {
        TargetLanguage.English => "english",
        TargetLanguage.German => "german",
        TargetLanguage.French => "french",
        TargetLanguage.Spanish => "spanish",
        TargetLanguage.Italian => "italian",
        TargetLanguage.Portuguese => "portuguese",
        TargetLanguage.Polish => "polish",
        TargetLanguage.Russian => "russian",
        TargetLanguage.Ukrainian => "ukrainian",
        TargetLanguage.Czech => "czech",
        TargetLanguage.ChineseSimplified => "schinese",
        TargetLanguage.Japanese => "japanese",
        TargetLanguage.Korean => "korean",
        TargetLanguage.Turkish => "turkish",
        TargetLanguage.Arabic => "arabic",
        _ => "english",
    };

    /// <summary>Anzeigename fuer den KI-Prompt: was die KI verstehen soll.</summary>
    public static string ToPromptName(this TargetLanguage lang) => lang switch
    {
        TargetLanguage.English => "English",
        TargetLanguage.German => "German",
        TargetLanguage.French => "French",
        TargetLanguage.Spanish => "Spanish",
        TargetLanguage.Italian => "Italian",
        TargetLanguage.Portuguese => "Portuguese",
        TargetLanguage.Polish => "Polish",
        TargetLanguage.Russian => "Russian",
        TargetLanguage.Ukrainian => "Ukrainian",
        TargetLanguage.Czech => "Czech",
        TargetLanguage.ChineseSimplified => "Simplified Chinese",
        TargetLanguage.Japanese => "Japanese",
        TargetLanguage.Korean => "Korean",
        TargetLanguage.Turkish => "Turkish",
        TargetLanguage.Arabic => "Arabic",
        _ => "English",
    };

    /// <summary>Nativer Anzeigename fuer die UI (was der User in seiner
    /// Sprache sieht).</summary>
    public static string ToNativeName(this TargetLanguage lang) => lang switch
    {
        TargetLanguage.English => "English",
        TargetLanguage.German => "Deutsch",
        TargetLanguage.French => "Français",
        TargetLanguage.Spanish => "Español",
        TargetLanguage.Italian => "Italiano",
        TargetLanguage.Portuguese => "Português",
        TargetLanguage.Polish => "Polski",
        TargetLanguage.Russian => "Русский",
        TargetLanguage.Ukrainian => "Українська",
        TargetLanguage.Czech => "Čeština",
        TargetLanguage.ChineseSimplified => "简体中文",
        TargetLanguage.Japanese => "日本語",
        TargetLanguage.Korean => "한국어",
        TargetLanguage.Turkish => "Türkçe",
        TargetLanguage.Arabic => "العربية",
        _ => lang.ToString(),
    };

    /// <summary>Unicode-Flag-Emoji per Regional-Indicator-Symbol-Paar
    /// (2 Zeichen ab U+1F1E6). Fuer Sprachen mit mehreren Laendern nehmen
    /// wir das Ursprungs-/Standardland: English → GB, Portuguese → PT,
    /// Spanish → ES, Chinese Simplified → CN, Arabic → SA.</summary>
    public static string ToFlagEmoji(this TargetLanguage lang) => lang switch
    {
        TargetLanguage.English => "🇬🇧",
        TargetLanguage.German => "🇩🇪",
        TargetLanguage.French => "🇫🇷",
        TargetLanguage.Spanish => "🇪🇸",
        TargetLanguage.Italian => "🇮🇹",
        TargetLanguage.Portuguese => "🇵🇹",
        TargetLanguage.Polish => "🇵🇱",
        TargetLanguage.Russian => "🇷🇺",
        TargetLanguage.Ukrainian => "🇺🇦",
        TargetLanguage.Czech => "🇨🇿",
        TargetLanguage.ChineseSimplified => "🇨🇳",
        TargetLanguage.Japanese => "🇯🇵",
        TargetLanguage.Korean => "🇰🇷",
        TargetLanguage.Turkish => "🇹🇷",
        TargetLanguage.Arabic => "🇸🇦",
        _ => "🏳️",
    };
}

/// <summary>Konfiguration fuer den Translation-Mod (E6). Der User waehlt
/// eine oder mehrere Zielsprachen; die KI uebersetzt alle Say-Texte und
/// Menu-Choices in diese Sprache(n) und wir schreiben Ren'Py-kompatible
/// <c>game/tl/&lt;lang&gt;/*.rpy</c>-Files. Im Spiel-Preferences-Menue
/// erscheint dann automatisch der Sprach-Selector — der Original bleibt
/// jederzeit anwaehlbar.
///
/// <see cref="SourceLanguage"/>: optional. Wenn <c>null</c>, wird der Prompt
/// mit "auto-detect from context" formuliert. Wenn gesetzt (z.B.
/// <see cref="TargetLanguage.English"/>), sagen wir der KI explizit was
/// die Ausgangssprache ist — praeziser aber weniger flexibel.
///
/// <see cref="TranslatedStrings"/>: pro Zielsprache eine Map "original →
/// uebersetzt" die der Preview-Dialog akzeptiert hat. Wenn null oder leer,
/// wird nichts deployed (User hat abgebrochen).</summary>
public sealed record TranslationConfig(
    IReadOnlyList<TargetLanguage> TargetLanguages,
    TargetLanguage? SourceLanguage = null,
    IReadOnlyDictionary<TargetLanguage, IReadOnlyDictionary<string, string>>? TranslatedStrings = null);
