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
/// The Merge Gate (§6) — the single entry point to the Wisdom tier. Mechanically: embed,
/// hybrid-search existing non-Retired Wisdom, insert on no match. On a match (cosine ≥ 0.80) the
/// <see cref="IMergeArbiter"/> rules: agreement merges the pair into a rewrite (reinforcement+1,
/// prior text versioned, cross-Project confirmation promoting to Global); contradiction
/// adjudicates by Supersede or Scope-split, leaving the survivors Contested.
/// </summary>
/// <remarks>
/// <see cref="AdmitAllAsync"/> is the whole interface: the gate owns the embedding round-trip,
/// the transaction, and the gate-wide advisory lock that serializes whole batches across every
/// caller and process — so ADR-0004's single-writer convergence rule is mechanism, not a comment,
/// and a caller cannot forget a transaction it never opens. The gate saves after every admission,
/// so the search's raw SQL is guaranteed to see what earlier candidates staged (§6's
/// merge-gate-as-reduce is enforced here, not left to callers), while nothing becomes visible
/// outside unless the whole batch commits — the §5 conversion marker and the §6 done marker ride
/// in as finalizers. A matched admission calls the arbiter — and re-embeds rewrites — inside that
/// transaction; the wait is accepted, since Postgres is local and the batch already holds the
/// lock. Arbiter failures propagate: the caller's retry (the §5 marker, the §6 queue) redoes the
/// Admission rather than letting a contradiction silently pass as a mechanical merge.
/// </remarks>
internal sealed class MergeGate(
    MimirDbContext db,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    WisdomSearch search,
    IMergeArbiter arbiter,
    IOptions<DistillationOptions> options,
    TimeProvider clock)
{
    /// <summary>
    /// The Merge Gate's advisory lock key — one gate-wide key ("mimir" in ASCII), taken with
    /// <c>pg_advisory_xact_lock</c> as the first statement of every Admission batch's
    /// transaction, never nested, released by Postgres on commit or rollback.
    /// </summary>
    private const long AdmissionLockKey = 0x6D696D6972;

    /// <summary>
    /// One Admission batch (see CONTEXT.md): the gate embeds every candidate text in a single
    /// round-trip, then admits the whole batch in its own transaction, serialized against every
    /// other batch — any caller, any process — by the advisory lock. All or nothing: a failure
    /// anywhere rolls back every admission and the finalizer's staged changes, clears the
    /// batch's entities from the change tracker, and propagates unchanged so the caller's retry
    /// semantics (the §5 marker, the §6 queue) still apply.
    /// </summary>
    /// <param name="finalizer">Runs inside the transaction; its staged changes commit atomically
    /// with the admissions. Callers stage their completion marker here.</param>
    public async Task AdmitAllAsync(
        IReadOnlyList<WisdomCandidate> candidates,
        Func<CancellationToken, Task>? finalizer,
        CancellationToken cancellationToken)
    {
        // Embeddings depend only on the text, so the batch embeds before the transaction opens
        // and holds the lock no longer than adjudication requires. Matched candidates still
        // reach the model inside it — arbiter rulings and rewrite re-embeddings depend on what
        // the search finds against staged rows.
        var vectors = candidates.Count == 0
            ? []
            : (await embeddings.GenerateAsync(
                    candidates.Select(c => c.Text), cancellationToken: cancellationToken))
                .Select(e => new Vector(e.Vector))
                .ToList();

        // Zip below truncates to the shorter side: a generator returning a short batch would
        // silently skip trailing candidates and still commit the marker — a permanent loss,
        // since the caller's retry keys off that marker. Fail loudly instead, before the lock.
        if (vectors.Count != candidates.Count)
        {
            throw new InvalidOperationException(
                $"the embedding batch returned {vectors.Count} vector(s) for {candidates.Count} candidate(s)");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await db.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_xact_lock({AdmissionLockKey})", cancellationToken);

            // The save after each admission stays inside this transaction: a later candidate's
            // search sees what earlier ones staged (§6's merge-gate-as-reduce), while nothing
            // becomes visible outside unless the whole batch commits.
            foreach (var (candidate, embedding) in candidates.Zip(vectors))
            {
                await AdmitAsync(candidate, embedding, cancellationToken);
            }

            if (finalizer is not null)
            {
                await finalizer(cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            // The rolled-back batch must not stay tracked on the shared scoped context: an
            // Added entity left behind would ride the caller's next save back in, bypassing
            // the gate, and a saved one would linger as a phantom the database no longer has.
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// One candidate's Admission, inside the batch's transaction and with the embedding the batch
    /// already generated for it.
    /// </summary>
    private async Task AdmitAsync(WisdomCandidate candidate, Vector embedding, CancellationToken cancellationToken)
    {
        var hits = await search.SearchAsync(embedding, candidate.Text, cancellationToken);

        // The §3 score-scale rule: the threshold reads the vector leg's best cosine, never the
        // RRF-fused score. Rows only the FTS leg surfaced carry no cosine and cannot match.
        var best = hits.Where(h => h.Cosine is not null).MaxBy(h => h.Cosine);
        if (best is null || best.Cosine < options.Value.MergeMatchThreshold)
        {
            Insert(candidate, embedding, WisdomVersionCause.Distilled);
        }
        else
        {
            var matched = await db.Wisdom.FirstAsync(w => w.Id == best.WisdomId, cancellationToken);
            var ruling = await arbiter.RuleAsync(matched, candidate, cancellationToken);
            switch (ruling)
            {
                case MergeRuling.Agreement agreement:
                    await MergeAsync(matched, candidate, agreement.MergedText, cancellationToken);
                    break;
                case MergeRuling.ScopeSplit split when ProjectInPlay(matched, candidate):
                    await SplitAsync(matched, candidate, split, cancellationToken);
                    break;
                default:
                    // Supersede — including a Scope-split ruled between two Global positions,
                    // where no Project exists to scope the split's project side to (§6.4).
                    Supersede(matched, candidate, embedding);
                    break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool ProjectInPlay(Wisdom matched, WisdomCandidate candidate)
        => matched.ScopeProjectId != Project.GlobalId || candidate.ScopeProjectId != Project.GlobalId;

    /// <summary>§6.2 no match: new Wisdom at reinforcement 1, version 1, with its Provenance.</summary>
    private Wisdom Insert(WisdomCandidate candidate, Vector embedding, WisdomVersionCause cause)
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
            Cause = cause,
        });
        foreach (var link in LinksOf(candidate))
        {
            db.Provenance.Add(NewProvenance(wisdom.Id, link));
        }

        return wisdom;
    }

    /// <summary>
    /// §6.3 agreement: reinforcement+1, <c>last_confirmed_at=now</c>, Provenance unioned, and the
    /// arbiter's rewrite becomes the current text (re-embedded, prior text versioned,
    /// <c>cause=merged</c>). Confirmation from a different Project promotes the scope to Global.
    /// </summary>
    private async Task MergeAsync(
        Wisdom wisdom, WisdomCandidate candidate, string mergedText, CancellationToken cancellationToken)
    {
        wisdom.Reinforcement++;
        wisdom.LastConfirmedAt = clock.GetUtcNow();

        // Promotion needs confirmation from a *different Project* (§6.3). A candidate proposing
        // Global scope carries no origin Project at all, so it cannot vouch for cross-Project
        // recurrence — only a candidate scoped to some other Project promotes.
        if (wisdom.ScopeProjectId != Project.GlobalId
            && candidate.ScopeProjectId != Project.GlobalId
            && candidate.ScopeProjectId != wisdom.ScopeProjectId)
        {
            wisdom.ScopeProjectId = Project.GlobalId;
        }

        await UnionProvenanceAsync(wisdom.Id, candidate, cancellationToken);
        if (mergedText != wisdom.Text)
        {
            await RewriteAsync(wisdom, mergedText, WisdomVersionCause.Merged, cancellationToken);
        }
    }

    /// <summary>
    /// §6.4 Supersede: the candidate is inserted as new Wisdom (Contested), and the loser is
    /// Retired with the <c>superseded_by</c> link — its text and chain untouched.
    /// </summary>
    private void Supersede(Wisdom wisdom, WisdomCandidate candidate, Vector embedding)
    {
        var successor = Insert(candidate, embedding, WisdomVersionCause.Adjudicated);
        successor.ContestedAt = successor.LastConfirmedAt;
        wisdom.SupersededBy = successor.Id;
        wisdom.RetiredAt = successor.LastConfirmedAt;
    }

    /// <summary>
    /// §6.4 Scope-split: the matched row keeps its own side of the split — a Global row stays
    /// Global, a Project row keeps its Project — and a sibling takes the other side, scoped to the
    /// candidate's Project when the sibling is the project side. Both rows carry the full
    /// provenance union and both are Contested; neither counts the contradiction as confirmation.
    /// </summary>
    private async Task SplitAsync(
        Wisdom wisdom, WisdomCandidate candidate, MergeRuling.ScopeSplit split, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var keepsGlobalSide = wisdom.ScopeProjectId == Project.GlobalId;
        var keptText = keepsGlobalSide ? split.GlobalText : split.ProjectText;
        var siblingText = keepsGlobalSide ? split.ProjectText : split.GlobalText;
        var siblingScope = keepsGlobalSide ? candidate.ScopeProjectId : Project.GlobalId;

        var links = await LinksInDbAsync(wisdom.Id, cancellationToken);
        var union = links.Union(LinksOf(candidate)).ToList();
        foreach (var link in union.Except(links))
        {
            db.Provenance.Add(NewProvenance(wisdom.Id, link));
        }

        wisdom.ContestedAt = now;
        if (keptText != wisdom.Text)
        {
            await RewriteAsync(wisdom, keptText, WisdomVersionCause.Adjudicated, cancellationToken);
        }

        var sibling = new Wisdom
        {
            Id = Guid.CreateVersion7(),
            Kind = wisdom.Kind,
            ScopeProjectId = siblingScope,
            Text = siblingText,
            Embedding = await EmbedAsync(siblingText, cancellationToken),
            Reinforcement = 1,
            LastConfirmedAt = now,
            ContestedAt = now,
        };
        db.Wisdom.Add(sibling);
        db.WisdomVersions.Add(new WisdomVersion
        {
            WisdomId = sibling.Id,
            Version = 1,
            Text = siblingText,
            CreatedAt = now,
            Cause = WisdomVersionCause.Adjudicated,
        });
        foreach (var link in union)
        {
            db.Provenance.Add(NewProvenance(sibling.Id, link));
        }
    }

    /// <summary>The new text becomes current — re-embedded, appended to the version chain.</summary>
    private async Task RewriteAsync(
        Wisdom wisdom, string text, WisdomVersionCause cause, CancellationToken cancellationToken)
    {
        wisdom.Text = text;
        wisdom.Embedding = await EmbedAsync(text, cancellationToken);

        // Version rows of a matched Wisdom are always flushed (the gate saves per admission),
        // so the max is authoritative on this connection.
        var latest = await db.WisdomVersions
            .Where(v => v.WisdomId == wisdom.Id)
            .MaxAsync(v => (int?)v.Version, cancellationToken) ?? 0;
        db.WisdomVersions.Add(new WisdomVersion
        {
            WisdomId = wisdom.Id,
            Version = latest + 1,
            Text = text,
            CreatedAt = clock.GetUtcNow(),
            Cause = cause,
        });
    }

    private async Task<Vector> EmbedAsync(string text, CancellationToken cancellationToken)
        => new(await embeddings.GenerateVectorAsync(text, cancellationToken: cancellationToken));

    /// <summary>
    /// Union semantics (§6): a link already recorded is not recorded again. Earlier candidates'
    /// rows are always flushed (the gate saves per admission), so the database check sees them —
    /// two sections of one HarvestedItem merging into one Wisdom is one provenance.
    /// </summary>
    private async Task UnionProvenanceAsync(
        Guid wisdomId, WisdomCandidate candidate, CancellationToken cancellationToken)
    {
        var recorded = await LinksInDbAsync(wisdomId, cancellationToken);
        foreach (var link in LinksOf(candidate).Except(recorded))
        {
            db.Provenance.Add(NewProvenance(wisdomId, link));
        }
    }

    private async Task<List<ProvenanceLink>> LinksInDbAsync(Guid wisdomId, CancellationToken cancellationToken)
        => await db.Provenance
            .Where(p => p.WisdomId == wisdomId)
            .Select(p => new ProvenanceLink(p.EpisodeId, p.EventId, p.HarvestedItemId))
            .ToListAsync(cancellationToken);

    private readonly record struct ProvenanceLink(Guid? EpisodeId, Guid? EventId, Guid? HarvestedItemId);

    /// <summary>
    /// One Provenance row per provenance Event (§6); no Events means one row. A candidate carrying
    /// nothing at all — a <c>mimir_remember</c> with no live Episode (§7.1) — yields no rows: born
    /// with the "orphaned provenance" the UI already flags, never an all-null link.
    /// </summary>
    private static IEnumerable<ProvenanceLink> LinksOf(WisdomCandidate candidate)
        => candidate.EventIds is { Count: > 0 }
            ? candidate.EventIds.Distinct()
                .Select(eventId => new ProvenanceLink(candidate.EpisodeId, eventId, candidate.HarvestedItemId))
            : candidate.EpisodeId is null && candidate.HarvestedItemId is null
                ? []
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
