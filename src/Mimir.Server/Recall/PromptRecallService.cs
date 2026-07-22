using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Recall;

/// <summary>
/// The Prompt lane (§7): relevant prompts pull Wisdom mid-session; most prompts inject nothing.
/// The prompt query-ranks the ambient candidate universe — the same §7 universe and native-content
/// exclusion as the Brief — and the gate opens only when the best eligible match reaches the
/// cosine threshold (a cosine, never a fused score, per §3). Every actual injection logs an
/// Injection row carrying the prompt as <c>query_context</c>; an empty decision leaves no trace.
/// </summary>
internal sealed class PromptRecallService(
    MimirDbContext db,
    QueryRanking ranking,
    IOptions<RecallOptions> options,
    TimeProvider clock)
{
    public async Task<string> ComposeInjectionAsync(
        string sessionId, Guid projectId, string prompt, CancellationToken cancellationToken)
    {
        // The ranking is scope-unfiltered by design and annotates ambient eligibility instead;
        // the lane keeps only the ambient candidate universe before anything else — an ineligible
        // match must not open the gate. The §3 search bounds the pool to its per-leg top-N first,
        // so a large foreign-Project corpus near the prompt can crowd an eligible match out of
        // both legs before this filter sees it. Accepted for v1; the §11 PerLegTopN knob widens
        // the pool if it bites.
        var ranked = await ranking.RankAsync(prompt, projectId, cancellationToken);
        var eligible = ranked.Where(r => r.AmbientEligible).ToList();

        // The gate demands an affirmative cosine at the threshold (§3: a cosine, never a fused
        // score). The lifted >= also holds the gate shut for null (off the vector leg) and for
        // NaN — pgvector's cosine of a degenerate zero-norm embedding.
        if (!eligible.Any(r => r.Cosine >= options.Value.PromptGateCosine))
        {
            return "";
        }

        var (injection, included) = InjectionRenderer.Render(
            eligible.Select(r => r.ToInjectionEntry()), options.Value.PromptBudgetChars);
        if (included.Count == 0)
        {
            return "";
        }

        InjectionLog.Record(
            db, sessionId, projectId, clock.GetUtcNow(),
            InjectionLane.Prompt, prompt, injection, included);
        await db.SaveChangesAsync(cancellationToken);
        return injection;
    }
}
