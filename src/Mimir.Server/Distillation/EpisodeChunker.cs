using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Distillation;

/// <summary>
/// §6 chunking: an Episode exceeding the context window is split chronologically into ~12K-token
/// windows and distilled per chunk — no reduce step, because the Merge Gate is the reduce.
/// Remember Events ride along in every chunk they could inform: they are small and salient by
/// definition, so every chunk carries all of them, in seq position.
/// </summary>
internal static class EpisodeChunker
{
    /// <summary>
    /// The chars-per-token estimate the budget is priced in. Crude and deliberately so: payloads
    /// are JSON-heavy English, qwen3's tokenizer averages 3–4 chars/token on that, and the §11
    /// budget leaves headroom inside the 16384 context for the estimate to be wrong.
    /// </summary>
    private const int CharsPerToken = 4;

    /// <summary>What one Event costs beyond its payload: the [eN] header line the prompt adds.</summary>
    private const int EventOverheadTokens = 16;

    public static IReadOnlyList<IReadOnlyList<Event>> Chunk(IReadOnlyList<Event> events, int chunkTokens)
    {
        var ordered = events.OrderBy(e => e.Seq).ToList();
        if (ordered.Count == 0)
        {
            return [];
        }

        if (ordered.Sum(EstimateTokens) <= chunkTokens)
        {
            return [ordered];
        }

        // Remembers are priced into every window up front; the max(…, 1) keeps a pathological
        // pile of Remembers from starving the windows into an infinite loop — an Event never
        // splits, so a window holds at least one no matter the budget.
        var remembers = ordered.Where(e => e.Type == EventType.Remember).ToList();
        var others = ordered.Where(e => e.Type != EventType.Remember).ToList();
        if (others.Count == 0)
        {
            return [remembers];
        }

        var window = Math.Max(chunkTokens - remembers.Sum(EstimateTokens), 1);
        var chunks = new List<IReadOnlyList<Event>>();
        var current = new List<Event>();
        var spent = 0;
        foreach (var evt in others)
        {
            var cost = EstimateTokens(evt);
            if (current.Count > 0 && spent + cost > window)
            {
                chunks.Add(WithRemembers(current, remembers));
                current = [];
                spent = 0;
            }

            current.Add(evt);
            spent += cost;
        }

        chunks.Add(WithRemembers(current, remembers));
        return chunks;
    }

    public static int EstimateTokens(Event evt) => EventOverheadTokens + (evt.Payload.Length / CharsPerToken);

    private static List<Event> WithRemembers(List<Event> window, List<Event> remembers)
        => [.. window.Concat(remembers).OrderBy(e => e.Seq)];
}
