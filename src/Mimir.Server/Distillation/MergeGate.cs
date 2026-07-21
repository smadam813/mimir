using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Pgvector;

namespace Mimir.Server.Distillation;

/// <summary>
/// One candidate at the gate: text with a kind, a scope, and where it came from. Harvested
/// candidates carry a HarvestedItem; the Distiller's carry their Episode and provenance Event
/// ids — plural, one Provenance row per Event (§6).
/// </summary>
internal sealed record WisdomCandidate(
    WisdomKind Kind,
    Guid ScopeProjectId,
    string Text,
    Guid? HarvestedItemId = null,
    Guid? EpisodeId = null,
    IReadOnlyList<Guid>? EventIds = null);

/// <summary>
/// The Merge Gate (§6) — the single entry point to the Wisdom tier — in its mechanical half: embed,
/// hybrid-search existing non-Retired Wisdom, and either insert new Wisdom or reinforce the match.
/// The LLM rewrite/adjudication ticket upgrades steps 3–4 in place; until then a match reinforces
/// mechanically and the existing text is kept.
/// </summary>
/// <remarks>
/// The gate saves after every admission, on the caller's <see cref="MimirDbContext"/> and inside
/// whatever transaction the caller opened — so the §5 conversion marker still commits atomically
/// with the Wisdom the item produced, while the search's raw SQL is guaranteed to see what earlier
/// candidates staged (the save-between-candidates rule is enforced here, not left to callers).
/// Admissions from separate scopes are not serialized against each other: near-duplicates admitted
/// concurrently could both insert. Fine while the harvest loop is the only caller; the Distiller
/// (#22) must keep §6's single-worker rule.
/// </remarks>
internal sealed class MergeGate(
    MimirDbContext db,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    WisdomSearch search,
    IOptions<DistillationOptions> options,
    TimeProvider clock)
{
    public async Task AdmitAsync(WisdomCandidate candidate, CancellationToken cancellationToken)
    {
        var embedding = new Vector(
            await embeddings.GenerateVectorAsync(candidate.Text, cancellationToken: cancellationToken));
        await AdmitAsync(candidate, embedding, cancellationToken);
    }

    /// <summary>
    /// Admission with a caller-supplied embedding, for callers that batch a whole item's
    /// embeddings in one round-trip before opening their transaction.
    /// </summary>
    public async Task AdmitAsync(WisdomCandidate candidate, Vector embedding, CancellationToken cancellationToken)
    {
        var hits = await search.SearchAsync(embedding, candidate.Text, cancellationToken);

        // The §3 score-scale rule: the threshold reads the vector leg's best cosine, never the
        // RRF-fused score. Rows only the FTS leg surfaced carry no cosine and cannot match.
        var best = hits.Where(h => h.Cosine is not null).MaxBy(h => h.Cosine);
        if (best is null || best.Cosine < options.Value.MergeMatchThreshold)
        {
            Insert(candidate, embedding);
        }
        else
        {
            await ReinforceAsync(best.WisdomId, candidate, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>§6.2 no match: new Wisdom at reinforcement 1, version 1, with its Provenance.</summary>
    private void Insert(WisdomCandidate candidate, Vector embedding)
    {
        var now = clock.GetUtcNow();
        var wisdom = new Wisdom
        {
            Id = Guid.CreateVersion7(),
            Kind = candidate.Kind,
            ScopeProjectId = candidate.ScopeProjectId,
            Text = candidate.Text,
            Embedding = embedding,
            Reinforcement = 1,
            LastConfirmedAt = now,
        };

        db.Wisdom.Add(wisdom);
        db.WisdomVersions.Add(new WisdomVersion
        {
            WisdomId = wisdom.Id,
            Version = 1,
            Text = candidate.Text,
            CreatedAt = now,
            Cause = WisdomVersionCause.Distilled,
        });
        foreach (var link in LinksOf(candidate))
        {
            db.Provenance.Add(NewProvenance(wisdom.Id, link));
        }
    }

    /// <summary>
    /// §6.3 match, mechanically: reinforcement+1, <c>last_confirmed_at=now</c>, Provenance
    /// unioned, existing text kept — so no new WisdomVersion either.
    /// </summary>
    private async Task ReinforceAsync(Guid wisdomId, WisdomCandidate candidate, CancellationToken cancellationToken)
    {
        var wisdom = await db.Wisdom.FirstAsync(w => w.Id == wisdomId, cancellationToken);
        wisdom.Reinforcement++;
        wisdom.LastConfirmedAt = clock.GetUtcNow();

        // Union semantics: a link already recorded is not recorded again. Earlier candidates'
        // rows are always flushed (the gate saves per admission), so the server-side check
        // sees them — two sections of one HarvestedItem merging into one Wisdom is one provenance.
        foreach (var link in LinksOf(candidate))
        {
            var recorded = await db.Provenance.AnyAsync(
                p => p.WisdomId == wisdomId
                    && p.HarvestedItemId == link.HarvestedItemId
                    && p.EpisodeId == link.EpisodeId
                    && p.EventId == link.EventId,
                cancellationToken);
            if (!recorded)
            {
                db.Provenance.Add(NewProvenance(wisdomId, link));
            }
        }
    }

    private readonly record struct ProvenanceLink(Guid? EpisodeId, Guid? EventId, Guid? HarvestedItemId);

    /// <summary>One Provenance row per provenance Event (§6); no Events means one row.</summary>
    private static IEnumerable<ProvenanceLink> LinksOf(WisdomCandidate candidate)
        => candidate.EventIds is { Count: > 0 }
            ? candidate.EventIds.Distinct()
                .Select(eventId => new ProvenanceLink(candidate.EpisodeId, eventId, candidate.HarvestedItemId))
            : [new ProvenanceLink(candidate.EpisodeId, null, candidate.HarvestedItemId)];

    private static Provenance NewProvenance(Guid wisdomId, ProvenanceLink link) => new()
    {
        Id = Guid.CreateVersion7(),
        WisdomId = wisdomId,
        EpisodeId = link.EpisodeId,
        EventId = link.EventId,
        HarvestedItemId = link.HarvestedItemId,
    };
}
