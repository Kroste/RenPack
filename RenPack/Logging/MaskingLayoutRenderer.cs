using System.Text;
using System.Text.RegularExpressions;
using NLog;
using NLog.Config;
using NLog.LayoutRenderers;
using NLog.LayoutRenderers.Wrappers;

namespace RenPack.Logging;

/// <summary>
/// Kroste-Standard: maskiert Passwörter/Tokens/Credentials in Log-Ausgaben.
/// Registriert als <c>${masked:inner=...}</c> in der nlog.config. Secrets dürfen
/// niemals im Klartext im Log landen — auch nicht auf Trace-Level.
/// </summary>
[LayoutRenderer("masked")]
[ThreadAgnostic]
public sealed class MaskingLayoutRenderer : WrapperLayoutRendererBase
{
    private static readonly (Regex Pattern, string Replacement)[] Rules =
    [
        // key=value / "key": "value" für sensible Schlüssel
        (new Regex(@"(?i)(password|passwort|token|secret|api[_-]?key|apikey|authorization|bearer|pwd)(\s*[:=]\s*""?)([^""\s,;)]+)",
            RegexOptions.Compiled), "$1$2***"),
        // Credentials in Connection-Strings / URLs: scheme://user:pass@host
        (new Regex(@"(?i)(://[^:/@\s]+:)([^@\s]+)(@)", RegexOptions.Compiled), "$1***$3"),
    ];

    protected override string Transform(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var (pattern, replacement) in Rules)
            text = pattern.Replace(text, replacement);
        return text;
    }

    /// <summary>Einmalig beim App-Start registrieren, bevor der erste Logger benutzt wird.</summary>
    public static void Register() =>
        LogManager.Setup().SetupExtensions(ext =>
            ext.RegisterLayoutRenderer<MaskingLayoutRenderer>("masked"));
}
