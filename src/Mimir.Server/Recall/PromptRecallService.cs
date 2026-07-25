using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Recall;

/// <summary>
/// The Prompt lane (§7): relevant prompts pull Wisdom mid-session; most prompts inject nothing.
/// The prompt query-ranks the ambient candidate universe — the same §7 universe and native-content
/// exclusion as the Brief — and the gate opens only when the best eligible match reaches the
/// cosine threshold (a cosine, never a fused score, per §3). Rendering and the §7 recording rules
/// belong to <see cref="InjectionLog"/>; this lane decides what to hand it, carrying the prompt as
/// <c>query_context</c> and the session's Project.
/// </summary>
internal sealed class PromptRecallService(
    QueryRanking ranking,
    InjectionLog injections,
    IOptions<RecallOptions> options)
{
    public async Task<string> ComposeInjectionAsync(
        string sessionId, Guid projectId, string prompt, CancellationToken cancellationToken)
    {
        // The ranking names the universe it searches, so every row back is ambient-eligible —
        // an ineligible match cannot reach the gate, and no foreign corpus can crowd an eligible
        // one out of the §3 search's per-leg top-N.
        var ranked = await ranking.RankAmbientAsync(prompt, projectId, cancellationToken);

        // The gate demands an affirmative cosine at the threshold (§3: a cosine, never a fused
        // score). The lifted >= also holds the gate shut for null (off the vector leg) and for
        // NaN — pgvector's cosine of a degenerate zero-norm embedding.
        if (!ranked.Any(r => r.Cosine >= options.Value.PromptGateCosine))
        {
            return "";
        }

        // This lane raises no notice, so nothing renders once the entries stop fitting: "" out is
        // the same empty injection an unopened gate returns.
        return await injections.RenderAndRecordAsync(
            new InjectionContext(InjectionLane.Prompt, sessionId, projectId, prompt),
            ranked.Select(r => r.ToInjectionEntry()),
            options.Value.PromptBudgetChars,
            notice: null,
            cancellationToken);
    }
}
