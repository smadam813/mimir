using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Distillation;

internal static class EpisodeChunker
{
    /// <summary>qwen3's tokenizer averages 3–4 chars/token on JSON-heavy English.</summary>
    private const int CharsPerToken = 4;

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

        // The max(…, 1) below is the loop guard: a pathological pile of Remembers would
        // otherwise price the window at zero and never advance.
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
