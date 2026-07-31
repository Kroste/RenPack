using System.Text.Json;

namespace RenPack.Services;

/// <summary>
/// Parst eine Zeile aus Ollamas NDJSON-Stream von <c>POST /api/pull</c>. Jede
/// Zeile ist ein eigenständiges JSON-Objekt; unbekannte oder leere Formate
/// liefern null, damit der Aufrufer sie einfach überspringen kann.
/// Übernommen aus Allpaca (<c>OllamaPullProgressParser.cs</c>).
/// </summary>
internal static class OllamaPullProgressParser
{
    public static OllamaPullEvent? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            // Fehlerantwort: { "error": "..." }
            if (root.TryGetProperty("error", out var errEl) &&
                errEl.GetString() is { Length: > 0 } err)
            {
                return new OllamaPullEvent("error", null, null, null,
                    IsError: true, ErrorMessage: err);
            }

            var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            long? completed = root.TryGetProperty("completed", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt64() : null;
            long? total = root.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number
                ? t.GetInt64() : null;
            var digest = root.TryGetProperty("digest", out var d) ? d.GetString() : null;

            return new OllamaPullEvent(status, completed, total, digest);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
