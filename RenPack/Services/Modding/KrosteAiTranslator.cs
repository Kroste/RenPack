using System.Text;
using System.Text.Json;
using NLog;

namespace RenPack.Services.Modding;

/// <summary>Fortschritts-Meldung waehrend die KI batch-uebersetzt.</summary>
public readonly record struct AiTranslateProgress(
    int Done, int Total, TargetLanguage CurrentLanguage);

/// <summary>
/// Batch-Uebersetzer via <see cref="IAiProvider"/> — analog zu
/// <see cref="KrosteAiRewriter"/> aber fuer Vollsprachuebersetzung.
/// Uebergibt der KI eine Liste von Strings + Zielsprache und bekommt
/// JSON-Map "original → translated" zurueck.
///
/// Batching: 30 Strings pro Request — bei kleineren Says schnell genug,
/// bei ganzen Story-Bloecken (500-1000 Says) kommen wir in vertretbarer
/// Zeit durch (Cloud-Provider: ~2-3s/Batch, Ollama: ~5-10s je nach Modell).
/// </summary>
public sealed class KrosteAiTranslator
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int BatchSize = 30;

    private readonly IAiProvider _provider;

    public KrosteAiTranslator(IAiProvider provider) => _provider = provider;

    /// <summary>Uebersetzt <paramref name="strings"/> in die Zielsprache
    /// und liefert eine Map original → uebersetzt. Duplicate strings in
    /// der Eingabe werden dedupliziert vor dem Provider-Call.</summary>
    public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        IReadOnlyList<string> strings,
        TargetLanguage targetLanguage,
        TargetLanguage? sourceLanguage = null,
        IProgress<AiTranslateProgress>? progress = null,
        CancellationToken ct = default)
    {
        var unique = strings
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unique.Count == 0) return new Dictionary<string, string>();

        Log.Info("KrosteAiTranslator: {n} unique strings → {lang}",
            unique.Count, targetLanguage);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        int done = 0;
        foreach (var batch in Batched(unique, BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new AiTranslateProgress(done, unique.Count, targetLanguage));
            try
            {
                var translations = await TranslateBatchAsync(batch, targetLanguage, sourceLanguage, ct);
                foreach (var (k, v) in translations)
                    if (!string.IsNullOrEmpty(v) && v != k) result[k] = v;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Uebersetzungs-Batch fehlgeschlagen — Batch wird uebersprungen");
            }
            done += batch.Count;
        }
        progress?.Report(new AiTranslateProgress(unique.Count, unique.Count, targetLanguage));
        return result;
    }

    private async Task<Dictionary<string, string>> TranslateBatchAsync(
        IReadOnlyList<string> batch, TargetLanguage target, TargetLanguage? source,
        CancellationToken ct)
    {
        var systemPrompt = BuildSystemPrompt(target, source);
        var userPrompt = BuildUserPrompt(batch);
        var response = await _provider.CompleteAsync(systemPrompt, userPrompt, ct);
        return ParseResponse(response, batch);
    }

    /// <summary>System-Prompt: Zielsprache, Regeln fuer Ren'Py-Tags und
    /// Escape-Sequenzen, JSON-Output-Format.</summary>
    internal static string BuildSystemPrompt(TargetLanguage target, TargetLanguage? source)
    {
        var sb = new StringBuilder();
        sb.Append("You are translating dialogue and menu lines from a Ren'Py visual novel");
        if (source is { } src)
            sb.Append($" from {src.ToPromptName()}");
        sb.AppendLine($" into {target.ToPromptName()}.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("  1. Translate the meaning naturally — do NOT translate literally word-by-word.");
        sb.AppendLine("  2. Preserve all escape sequences literally: \\\" \\n \\t \\\\ must stay as-is.");
        sb.AppendLine("  3. Preserve Ren'Py text tags exactly: {i}...{/i}, {b}...{/b}, {color=#xxx}...{/color},");
        sb.AppendLine("     {size=+2}...{/size}, {a=url}...{/a}, {w=1.5}, {p}, {nw} etc.");
        sb.AppendLine("  4. Preserve variable interpolations: [player_name], [amount!t], [char.name] etc. —");
        sb.AppendLine("     the content in square brackets stays unchanged.");
        sb.AppendLine("  5. Preserve line breaks and roughly the same length.");
        sb.AppendLine("  6. Return a JSON object mapping the input index (as string) to the translated text.");
        sb.AppendLine("     Format: {\"0\": \"translation\", \"1\": \"translation\", ...}");
        sb.AppendLine("     If a line cannot be translated (e.g. it's already in the target language,");
        sb.AppendLine("     or it's just a variable reference like \"[name]\"), omit its index.");
        return sb.ToString();
    }

    internal static string BuildUserPrompt(IReadOnlyList<string> batch)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Translate these lines:");
        sb.Append("[\n");
        for (int i = 0; i < batch.Count; i++)
        {
            sb.Append("  {\"index\": ").Append(i)
              .Append(", \"text\": ").Append(JsonSerializer.Serialize(batch[i]))
              .Append('}');
            if (i < batch.Count - 1) sb.Append(',');
            sb.Append('\n');
        }
        sb.Append(']');
        return sb.ToString();
    }

    internal static Dictionary<string, string> ParseResponse(
        string response, IReadOnlyList<string> batch)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var cleaned = ExtractJson(response);
        if (string.IsNullOrWhiteSpace(cleaned)) return result;
        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!int.TryParse(prop.Name, out int idx) || idx < 0 || idx >= batch.Count)
                    continue;
                var translated = prop.Value.GetString();
                if (string.IsNullOrEmpty(translated)) continue;
                result[batch[idx]] = translated;
            }
        }
        catch (JsonException ex)
        {
            Log.Warn(ex, "Uebersetzungs-JSON parse fehlgeschlagen. Snippet: {snip}",
                cleaned[..Math.Min(200, cleaned.Length)]);
        }
        return result;
    }

    private static string ExtractJson(string raw)
    {
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```"))
        {
            int firstNl = cleaned.IndexOf('\n');
            if (firstNl > 0) cleaned = cleaned[(firstNl + 1)..];
            int fenceEnd = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd > 0) cleaned = cleaned[..fenceEnd];
        }
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

    private static IEnumerable<IReadOnlyList<T>> Batched<T>(IReadOnlyList<T> src, int size)
    {
        for (int i = 0; i < src.Count; i += size)
        {
            int end = Math.Min(i + size, src.Count);
            yield return src.GetRange(i, end - i);
        }
    }

    /// <summary>Sammelt alle einzigartigen uebersetzbaren Strings aus der
    /// Analyse — Says + Menu-Choices. Reihenfolge stabilisieren wir per
    /// Path/Line, damit der Preview-Dialog eine sinnvolle Ordnung zeigt.</summary>
    public static IReadOnlyList<string> CollectTranslatableStrings(ModAnalysis analysis)
    {
        var strings = new List<string>();
        foreach (var s in analysis.SayStatements)
            strings.Add(s.RawTextInFile);
        foreach (var c in analysis.Choices)
            strings.Add(c.Text);
        return strings
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}

internal static class ListRangeExt
{
    public static List<T> GetRange<T>(this IReadOnlyList<T> list, int index, int count)
    {
        var result = new List<T>(count);
        for (int i = 0; i < count; i++) result.Add(list[index + i]);
        return result;
    }
}
