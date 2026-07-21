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
/// candidates carry a HarvestedItem; the Distiller's will carry Episode/Event ids.
/// </summary>
internal sealed record WisdomCandidate(
    WisdomKind Kind,
    Guid ScopeProjectId,
    string Text,
    Guid? HarvestedItemId = null,
    Guid? EpisodeId = null,
    Guid? EventId = null);

/// <summary>
/// The Merge Gate (§6) — the single entry point to the Wisdom tier — in its mechanical half: embed,
/// hybrid-search existing non-Retired Wisdom, and either insert new Wisdom or reinforce the match.
/// The LLM rewrite/adjudication ticket upgrades steps 3–4 in place; until then a match reinforces
/// mechanically and the existing text is kept.
/// </summary>
/// <remarks>
/// The gate stages changes on the caller's <see cref="MimirDbContext"/> and never saves — the
/// caller owns the transaction, which is what lets the §5 conversion marker commit atomically
/// with the Wisdom the item produced. Callers must save between candidates (inside their
/// transaction) so the search's raw SQL sees what earlier candidates staged.
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
        db.Provenance.Add(ProvenanceOf(candidate, wisdom.Id));
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

        // Union semantics: a link already recorded is not recorded again. Staged rows count
        // too — two sections of one HarvestedItem merging into one Wisdom is one provenance.
        // The EF-translatable predicate below must stay the mirror of IsSameProvenance.
        var recorded = db.Provenance.Local.Any(p => IsSameProvenance(p, candidate, wisdomId))
            || await db.Provenance.AnyAsync(
                p => p.WisdomId == wisdomId
                    && p.HarvestedItemId == candidate.HarvestedItemId
                    && p.EpisodeId == candidate.EpisodeId
                    && p.EventId == candidate.EventId,
                cancellationToken);
        if (!recorded)
        {
            db.Provenance.Add(ProvenanceOf(candidate, wisdomId));
        }
    }

    private static Provenance ProvenanceOf(WisdomCandidate candidate, Guid wisdomId) => new()
    {
        Id = Guid.CreateVersion7(),
        WisdomId = wisdomId,
        EpisodeId = candidate.EpisodeId,
        EventId = candidate.EventId,
        HarvestedItemId = candidate.HarvestedItemId,
    };

    private static bool IsSameProvenance(Provenance provenance, WisdomCandidate candidate, Guid wisdomId)
        => provenance.WisdomId == wisdomId
            && provenance.HarvestedItemId == candidate.HarvestedItemId
            && provenance.EpisodeId == candidate.EpisodeId
            && provenance.EventId == candidate.EventId;
}
