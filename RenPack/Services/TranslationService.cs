using NLog;

namespace RenPack.Services;

/// <summary>
/// Übersetzt Variablennamen in Batches. Cached bereits übersetzte Namen im
/// Speicher (pro Provider+Sprache-Kombination), damit derselbe Name nicht
/// mehrmals angefragt wird — auch nicht innerhalb einer Session, wenn der
/// Nutzer mehrere Saves des gleichen Spiels öffnet.
/// </summary>
public sealed class TranslationService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private const int BatchSize = 25;

    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private string _cacheKeyPrefix = "";

    /// <summary>Setzt den Cache zurück, wenn sich Provider oder Zielsprache
    /// geändert haben — sonst bleibt der Cache stehen (spart Anfragen zwischen
    /// mehreren Saves).</summary>
    public void ResetCacheIfNeeded(string providerName, string targetLanguage)
    {
        var newPrefix = $"{providerName}|{targetLanguage}|";
        if (_cacheKeyPrefix == newPrefix) return;
        _cache.Clear();
        _cacheKeyPrefix = newPrefix;
    }

    /// <summary>Vorher bereits übersetzte Namen aus dem Cache liefern.</summary>
    public bool TryGetCached(string name, out string translation)
        => _cache.TryGetValue(_cacheKeyPrefix + name, out translation!);

    /// <summary>Übersetzt alle noch nicht gecachten Namen und aktualisiert den
    /// Cache. Der optionale Progress-Callback wird nach jedem Batch aufgerufen
    /// (done, total).</summary>
    public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        IAiProvider provider, IReadOnlyList<string> names, string targetLanguage,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var uncached = new List<string>();
        var result = new Dictionary<string, string>(names.Count, StringComparer.Ordinal);
        foreach (var n in names)
        {
            if (_cache.TryGetValue(_cacheKeyPrefix + n, out var hit)) result[n] = hit;
            else uncached.Add(n);
        }

        if (uncached.Count == 0)
        {
            Log.Debug("Alle {count} Namen im Cache", names.Count);
            return result;
        }
        Log.Info("Übersetze {new} neue Namen (von {total}) via {provider}",
            uncached.Count, names.Count, provider.Name);

        int done = 0;
        for (int i = 0; i < uncached.Count; i += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = uncached.Skip(i).Take(BatchSize).ToList();
            try
            {
                var batchResult = await provider.TranslateBatchAsync(chunk, targetLanguage, cancellationToken);
                foreach (var (k, v) in batchResult)
                {
                    _cache[_cacheKeyPrefix + k] = v;
                    result[k] = v;
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Übersetzungs-Batch fehlgeschlagen (Namen: {names})",
                    string.Join(", ", chunk));
                // Bei Fehler einfach Batch überspringen — restliche Batches probieren.
            }
            done += chunk.Count;
            progress?.Report((done, uncached.Count));
        }
        return result;
    }
}
