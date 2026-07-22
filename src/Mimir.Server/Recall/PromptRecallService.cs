using Microsoft.EntityFrameworkCore;
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
        var ranked = await ranking.RankAsync(prompt, projectId, cancellationToken);
        if (ranked.Count == 0)
        {
            return "";
        }

        // The ranking is scope-unfiltered by design; the lane narrows its hits to the ambient
        // candidate universe before anything else — an ineligible match must not open the gate.
        var hitIds = ranked.Select(r => r.WisdomId).ToList();
        var eligibleIds = await AmbientCandidates.Of(db, projectId)
            .Where(w => hitIds.Contains(w.Id))
            .Select(w => w.Id)
            .ToListAsync(cancellationToken);
        var eligible = ranked.Where(r => eligibleIds.Contains(r.WisdomId)).ToList();

        var bestCosine = eligible.Max(r => r.Cosine);
        if (bestCosine is null || bestCosine < options.Value.PromptGateCosine)
        {
            return "";
        }

        var entries = eligible.Select(r => new InjectionEntry(
            r.WisdomId,
            r.Score,
            r.Kind,
            r.ScopeProjectId == Project.GlobalId,
            r.LastConfirmedAt,
            r.Text));
        var (injection, included) = InjectionRenderer.Render(entries, options.Value.PromptBudgetChars);
        if (included.Count == 0)
        {
            return "";
        }

        db.Injections.Add(new Injection
        {
            Id = Guid.CreateVersion7(),
            SessionId = sessionId,
            ProjectId = projectId,
            At = clock.GetUtcNow(),
            Lane = InjectionLane.Prompt,
            QueryContext = prompt,
            Chars = injection.Length,
            Items = included
                .Select(e => new InjectionItem { WisdomId = e.WisdomId, Score = e.Score })
                .ToList(),
        });
        await db.SaveChangesAsync(cancellationToken);
        return injection;
    }
}
