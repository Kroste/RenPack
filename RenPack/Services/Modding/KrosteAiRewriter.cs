using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLog;

namespace RenPack.Services.Modding;

/// <summary>
/// Nutzt den konfigurierten <see cref="IAiProvider"/>, um in Say-Body-
/// Texten alte Character-Namen konsistent zu ersetzen — inklusive
/// Grammatik (Possessivformen, Genitiv im Deutschen etc.).
///
/// **Wann brauchen wir das?** Der E4-MVP (v0.11.0) tauscht nur den
/// Anzeigenamen des Character-Objekts — im Body-Text bleibt der alte
/// Name stehen (<c>"Hi Sophia, wie geht's?"</c>). Bei E4b optional an-
/// schaltbar via Rename-Config-Checkbox: die KI schreibt betroffene
/// Say-Zeilen um.
///
/// **Sicherheit:** Der Rewriter liefert NUR Vorschlaege — der Deploy
/// passiert erst nach User-Bestaetigung im Preview-Dialog. Nicht jeder
/// Vorschlag wird angewendet.
/// </summary>
public sealed class KrosteAiRewriter
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Max Anzahl Say-Zeilen pro AI-Batch — mehr wird zu langsam
    /// und zu teuer bei Cloud-Providern. 20-25 ist ein guter Wert
    /// (analog zu TranslationService).</summary>
    private const int BatchSize = 20;

    private readonly IAiProvider _provider;

    public KrosteAiRewriter(IAiProvider provider) => _provider = provider;

    /// <summary>Findet alle Say-Statements deren Body-Text mind. einen der
    /// zu ersetzenden Character-Namen erwaehnt (Word-Boundary-Match), und
    /// fragt die KI um konsistent umgeschriebene Fassungen.
    ///
    /// Rueckgabe: Liste von <see cref="BodyTextEdit"/> mit den Vorschlaegen.
    /// User bekommt die im Preview-Dialog gezeigt und kann pro Zeile
    /// akzeptieren.</summary>
    public async Task<IReadOnlyList<BodyTextEdit>> ProposeRewritesAsync(
        IReadOnlyList<RpySayStatement> allSays,
        IReadOnlyDictionary<string, string> nameMappings,
        IProgress<AiRewriteProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (nameMappings.Count == 0) return [];

        // Kandidaten filtern: nur Says die mindestens einen alten Namen
        // enthalten. Word-Boundary damit "Sam" nicht "Samsung" trifft.
        var oldNames = nameMappings.Keys
            .Where(k => !string.IsNullOrWhiteSpace(nameMappings[k]))
            .ToList();
        if (oldNames.Count == 0) return [];

        // Aus dem alten NAMEN (DisplayName) matchen — hier ist der Trick:
        // nameMappings-Key ist der VAR-Name (sophia), aber im Body-Text
        // steht der DISPLAY-Name (Sophia). Der Aufrufer muss diese Zuordnung
        // machen bevor er uns die Mappings gibt. Wir arbeiten hier
        // ausschliesslich mit Display-Namen. Der Vertrag ist klar:
        // nameMappings = { "OldDisplayName": "NewDisplayName" }.
        var boundaryPattern = BuildNameBoundaryRegex(oldNames);
        var candidates = allSays
            .Where(s => boundaryPattern.IsMatch(s.RawTextInFile))
            .ToList();

        Log.Info("KrosteAiRewriter: {n} Says von {total} enthalten die {m} umzubenennenden Namen",
            candidates.Count, allSays.Count, oldNames.Count);
        if (candidates.Count == 0) return [];

        var results = new List<BodyTextEdit>(candidates.Count);
        int done = 0;
        foreach (var batch in Batched(candidates, BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new AiRewriteProgress(done, candidates.Count));
            try
            {
                var rewrites = await ProcessBatchAsync(batch, nameMappings, ct);
                results.AddRange(rewrites);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "KI-Batch fehlgeschlagen — Batch wird uebersprungen");
                // Kein Throw: der Preview-Dialog zeigt einfach weniger Vorschlaege,
                // aber der Build kann weiterlaufen.
            }
            done += batch.Count;
        }
        progress?.Report(new AiRewriteProgress(candidates.Count, candidates.Count));
        return results;
    }

    private async Task<List<BodyTextEdit>> ProcessBatchAsync(
        IReadOnlyList<RpySayStatement> batch,
        IReadOnlyDictionary<string, string> mappings,
        CancellationToken ct)
    {
        var systemPrompt = BuildSystemPrompt(mappings);
        var userPrompt = BuildUserPrompt(batch);
        var response = await _provider.CompleteAsync(systemPrompt, userPrompt, ct);
        return ParseResponse(response, batch);
    }

    /// <summary>System-Prompt: gibt der KI die Rolle + die Mappings + die
    /// Constraints. Kritisch: KI soll NUR den Text umschreiben (kein Meta-
    /// Kommentar), Escape-Sequenzen (\", \n) beibehalten, JSON zurueckgeben.</summary>
    internal static string BuildSystemPrompt(IReadOnlyDictionary<string, string> mappings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are rewriting dialogue lines from a Ren'Py visual novel to replace character names consistently.");
        sb.AppendLine("Apply these name replacements everywhere they appear, including possessive forms (Sophia's → Anna's) and grammatical variants:");
        foreach (var (from, to) in mappings.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)))
            sb.AppendLine($"  - \"{from}\" → \"{to}\"");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("  1. Preserve all escape sequences literally: \\\" \\n \\t \\\\ must stay as-is.");
        sb.AppendLine("  2. Preserve Ren'Py text tags: {i}...{/i}, {color=...}...{/color}, [var_name] etc. — do not touch them.");
        sb.AppendLine("  3. Only rewrite lines that actually mention a name from the list. Leave others exactly as input.");
        sb.AppendLine("  4. Do not change meaning, tone or length beyond the name substitution.");
        sb.AppendLine("  5. Return a JSON object mapping the input index (as string) to the rewritten text.");
        sb.AppendLine("     Format: {\"0\": \"new text\", \"1\": \"new text\", ...}");
        sb.AppendLine("     If a line does not need rewriting, omit its index from the output.");
        return sb.ToString();
    }

    /// <summary>User-Prompt: liste der Zeilen als JSON-Array — index bleibt
    /// stabil damit die Antwort zuordbar ist.</summary>
    internal static string BuildUserPrompt(IReadOnlyList<RpySayStatement> batch)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Rewrite these dialogue lines according to the rules. Input:");
        sb.Append("[\n");
        for (int i = 0; i < batch.Count; i++)
        {
            sb.Append("  {\"index\": ").Append(i)
              .Append(", \"text\": ").Append(JsonSerializer.Serialize(batch[i].RawTextInFile))
              .Append('}');
            if (i < batch.Count - 1) sb.Append(',');
            sb.Append('\n');
        }
        sb.Append(']');
        return sb.ToString();
    }

    internal static List<BodyTextEdit> ParseResponse(string response,
        IReadOnlyList<RpySayStatement> batch)
    {
        var results = new List<BodyTextEdit>();
        // Erst versuchen, das erste {...}-Objekt aus der Antwort zu extrahieren.
        // Manche Modelle wrappen JSON in Markdown-Code-Fences.
        string cleaned = ExtractJson(response);
        if (string.IsNullOrWhiteSpace(cleaned)) return results;

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!int.TryParse(prop.Name, out int idx) || idx < 0 || idx >= batch.Count)
                    continue;
                var newText = prop.Value.GetString();
                if (string.IsNullOrEmpty(newText)) continue;
                var orig = batch[idx];
                if (newText == orig.RawTextInFile) continue;
                results.Add(new BodyTextEdit(
                    orig.SourceFile, orig.SourceLine,
                    orig.RawTextInFile, newText,
                    Accepted: true));
            }
        }
        catch (JsonException ex)
        {
            Log.Warn(ex, "KI-Antwort ist kein valides JSON — Batch verworfen. Snippet: {snip}",
                cleaned[..Math.Min(200, cleaned.Length)]);
        }
        return results;
    }

    private static string ExtractJson(string raw)
    {
        // Markdown-Code-Fences entfernen falls vorhanden.
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```"))
        {
            int firstNl = cleaned.IndexOf('\n');
            if (firstNl > 0) cleaned = cleaned[(firstNl + 1)..];
            int fenceEnd = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd > 0) cleaned = cleaned[..fenceEnd];
        }
        // Erstes { bis passendes } — robuster als naiver Parse fuer
        // Antworten mit Praefix-Text.
        int start = cleaned.IndexOf('{');
        if (start < 0) return "";
        int depth = 0;
        for (int i = start; i < cleaned.Length; i++)
        {
            if (cleaned[i] == '{') depth++;
            else if (cleaned[i] == '}')
            {
                depth--;
                if (depth == 0) return cleaned[start..(i + 1)];
            }
        }
        return "";
    }

    private static Regex BuildNameBoundaryRegex(IReadOnlyList<string> names)
    {
        // Escape jeden Namen (falls jemand ein Regex-Metachar im Namen hat)
        // und packe sie in eine Alternation mit Wortgrenzen.
        var alternatives = string.Join("|", names.Select(Regex.Escape));
        return new Regex($@"\b(?:{alternatives})\b", RegexOptions.Compiled);
    }

    private static IEnumerable<IReadOnlyList<T>> Batched<T>(IReadOnlyList<T> src, int size)
    {
        for (int i = 0; i < src.Count; i += size)
            yield return src.Skip(i).Take(size).ToList();
    }
}

/// <summary>Ein von der KI vorgeschlagenes Body-Text-Edit einer Say-Zeile.
/// <see cref="Accepted"/> ist Default <c>true</c> — im Preview-Dialog kann
/// der User einzelne Vorschlaege ausschalten.</summary>
public sealed record BodyTextEdit(
    string SourceFile,
    int SourceLine,
    string OriginalText,
    string NewText,
    bool Accepted);

/// <summary>Progress-Info fuer den Rewrite-Aufruf: Anzahl abgearbeiteter
/// Says von den Kandidaten. UI zeigt Fortschrittsbalken.</summary>
public sealed record AiRewriteProgress(int Done, int Total);
