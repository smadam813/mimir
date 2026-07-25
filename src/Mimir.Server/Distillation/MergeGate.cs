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
/// <see cref="AdmitAllAsync"/> and <see cref="EditAsync"/> are the whole interface, and each runs
/// on a context of its own making, in its own transaction, under one gate-wide advisory lock — so
/// ADR-0004's rule that nothing else writes Wisdom text is mechanism, and a caller supplies text
/// and nothing else. Rollback is the dispose: a failure leaves no residue in the caller. §10's
/// other curation writes — retire, unretire, delete — change a row's standing, not its words, and
/// stay outside. Arbiter failures propagate rather than degrading to a mechanical merge; the
/// caller's retry (the §5 marker, the §6 queue) redoes the Admission.
/// </remarks>
internal sealed class MergeGate(
    IDbContextFactory<MimirDbContext> contexts,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    IOptions<SearchOptions> searchOptions,
    IMergeArbiter arbiter,
    IOptions<DistillationOptions> options,
    TimeProvider clock)
{
    /// <summary>
    /// The Merge Gate's advisory lock key — one gate-wide key ("mimir" in ASCII), taken with
    /// <c>pg_advisory_xact_lock</c> as the first statement of every Admission batch's and every
    /// edit's transaction, never nested, released by Postgres on commit or rollback.
    /// </summary>
    internal const long AdmissionLockKey = 0x6D696D6972;

    /// <summary>
    /// One Admission batch (see CONTEXT.md): the gate embeds every candidate text in a single
    /// round-trip, then admits the whole batch on its own context, in its own transaction,
    /// serialized against every other batch — any caller, any process — by the advisory lock. All
    /// or nothing: a failure anywhere rolls back every admission and the finalizer's writes and
    /// propagates unchanged, so the caller's retry semantics (the §5 marker, the §6 queue) still
    /// apply. The batch context is disposed either way, so a failed batch cannot reach into the
    /// caller's own change tracker.
    /// </summary>
    /// <param name="finalizer">Runs inside the transaction, on the batch's context; what it writes
    /// commits atomically with the admissions. Callers write their completion marker here — and,
    /// written there, it leaves any copy of that row the caller tracks stale. Benign: nothing
    /// writes those copies back.</param>
    public async Task AdmitAllAsync(
        IReadOnlyList<WisdomCandidate> candidates,
        Func<MimirDbContext, CancellationToken, Task>? finalizer,
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

        // The batch's own context, and a search bound to it, so an admission's search sees what
        // earlier admissions of the same batch staged — the scoped WisdomSearch the recall lanes
        // share runs on a connection this transaction is invisible to.
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var search = new WisdomSearch(db, searchOptions);

        // Rollback is the dispose: nothing below is visible until the commit on the last line,
        // and everything the batch tracked dies with the context.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // A batch that admits nothing has nothing to serialize — an Episode that yielded no
        // candidates, a frontmatter-only file — and its finalizer only touches its own caller's
        // row. Taking the gate-wide lock for that would make a Backfill's worth of sparse files
        // contend with real batches, interactive saves included, for no convergence benefit.
        if (candidates.Count > 0)
        {
            await db.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_xact_lock({AdmissionLockKey})", cancellationToken);
        }

        // The save after each admission stays inside this transaction: a later candidate's
        // search sees what earlier ones staged (§6's merge-gate-as-reduce), while nothing
        // becomes visible outside unless the whole batch commits.
        foreach (var (candidate, embedding) in candidates.Zip(vectors))
        {
            await AdmitAsync(db, search, candidate, embedding, cancellationToken);
        }

        if (finalizer is not null)
        {
            await finalizer(db, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// The §8.1 edit, through the gate because it rewrites a Wisdom's words: the new text becomes
    /// current — re-embedded, appended to the chain as a <c>cause=edited</c> WisdomVersion — under
    /// the same advisory lock an Admission batch takes, so an interactive edit and a background
    /// rewrite cannot both claim the same version number. Reinforcement and recency are untouched:
    /// an edit rewords, only an Admission confirms (§6). A blank or unchanged text, or a Wisdom
    /// already deleted, is a no-op.
    /// </summary>
    public async Task EditAsync(Guid wisdomId, string text, CancellationToken cancellationToken)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        // Before the lock, like a batch's candidates: an edit's text is known up front, so its
        // model round-trip need not be held against every background batch. (A batch's *rewrites*
        // must embed inside the lock — the arbiter invents that text mid-transaction.) The price
        // is one wasted embedding when the edit turns out to be a no-op, paid locally.
        var embedding = await EmbedAsync(trimmed, cancellationToken);

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({AdmissionLockKey})", cancellationToken);

        // Read under the lock: an edit that raced a batch would otherwise reword text the batch
        // is in the middle of replacing, and number its version off a chain about to grow.
        var wisdom = await db.Wisdom.FirstOrDefaultAsync(w => w.Id == wisdomId, cancellationToken);
        if (wisdom is null || wisdom.Text == trimmed)
        {
            return;
        }

        await RewriteAsync(db, wisdom, trimmed, embedding, WisdomVersionCause.Edited, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// One candidate's Admission, inside the batch's transaction and with the embedding the batch
    /// already generated for it.
    /// </summary>
    private async Task AdmitAsync(
        MimirDbContext db,
        WisdomSearch search,
        WisdomCandidate candidate,
        Vector embedding,
        CancellationToken cancellationToken)
    {
        var hits = await search.SearchAsync(embedding, candidate.Text, cancellationToken);

        // The §3 score-scale rule: the threshold reads the vector leg's best cosine, never the
        // RRF-fused score. Rows only the FTS leg surfaced carry no cosine and cannot match.
        var best = hits.Where(h => h.Cosine is not null).MaxBy(h => h.Cosine);
        if (best is null || best.Cosine < options.Value.MergeMatchThreshold)
        {
            Insert(db, candidate, embedding, WisdomVersionCause.Distilled);
        }
        else
        {
            var matched = await db.Wisdom.FirstAsync(w => w.Id == best.WisdomId, cancellationToken);
            var ruling = await arbiter.RuleAsync(matched, candidate, cancellationToken);
            switch (ruling)
            {
                case MergeRuling.Agreement agreement:
                    await MergeAsync(db, matched, candidate, agreement.MergedText, cancellationToken);
                    break;
                case MergeRuling.ScopeSplit split when ProjectInPlay(matched, candidate):
                    await SplitAsync(db, matched, candidate, split, cancellationToken);
                    break;
                default:
                    // Supersede — including a Scope-split ruled between two Global positions,
                    // where no Project exists to scope the split's project side to (§6.4).
                    Supersede(db, matched, candidate, embedding);
                    break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool ProjectInPlay(Wisdom matched, WisdomCandidate candidate)
        => matched.ScopeProjectId != Project.GlobalId || candidate.ScopeProjectId != Project.GlobalId;

    /// <summary>§6.2 no match: new Wisdom at reinforcement 1, version 1, with its Provenance.</summary>
    private Wisdom Insert(
        MimirDbContext db, WisdomCandidate candidate, Vector embedding, WisdomVersionCause cause)
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
        MimirDbContext db,
        Wisdom wisdom,
        WisdomCandidate candidate,
        string mergedText,
        CancellationToken cancellationToken)
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

        await UnionProvenanceAsync(db, wisdom.Id, candidate, cancellationToken);
        if (mergedText != wisdom.Text)
        {
            // The arbiter invented this text just now, so its embedding is the one round-trip the
            // gate cannot hoist out of the lock — the accepted wait (§6).
            await RewriteAsync(
                db,
                wisdom,
                mergedText,
                await EmbedAsync(mergedText, cancellationToken),
                WisdomVersionCause.Merged,
                cancellationToken);
        }
    }

    /// <summary>
    /// §6.4 Supersede: the candidate is inserted as new Wisdom (Contested), and the loser is
    /// Retired with the <c>superseded_by</c> link — its text and chain untouched.
    /// </summary>
    private void Supersede(
        MimirDbContext db, Wisdom wisdom, WisdomCandidate candidate, Vector embedding)
    {
        var successor = Insert(db, candidate, embedding, WisdomVersionCause.Adjudicated);
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
        MimirDbContext db,
        Wisdom wisdom,
        WisdomCandidate candidate,
        MergeRuling.ScopeSplit split,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var keepsGlobalSide = wisdom.ScopeProjectId == Project.GlobalId;
        var keptText = keepsGlobalSide ? split.GlobalText : split.ProjectText;
        var siblingText = keepsGlobalSide ? split.ProjectText : split.GlobalText;
        var siblingScope = keepsGlobalSide ? candidate.ScopeProjectId : Project.GlobalId;

        var links = await LinksInDbAsync(db, wisdom.Id, cancellationToken);
        var union = links.Union(LinksOf(candidate)).ToList();
        foreach (var link in union.Except(links))
        {
            db.Provenance.Add(NewProvenance(wisdom.Id, link));
        }

        wisdom.ContestedAt = now;
        if (keptText != wisdom.Text)
        {
            await RewriteAsync(
                db,
                wisdom,
                keptText,
                await EmbedAsync(keptText, cancellationToken),
                WisdomVersionCause.Adjudicated,
                cancellationToken);
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

    /// <summary>
    /// The one keeper of a rewrite: the new text becomes current, with its embedding, appended to
    /// the version chain. Every path that changes a Wisdom's words comes through here, holding the
    /// advisory lock, so the <c>(wisdom_id, version)</c> chain only ever has one writer.
    /// </summary>
    private async Task RewriteAsync(
        MimirDbContext db,
        Wisdom wisdom,
        string text,
        Vector embedding,
        WisdomVersionCause cause,
        CancellationToken cancellationToken)
    {
        wisdom.Text = text;
        wisdom.Embedding = embedding;

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
    private static async Task UnionProvenanceAsync(
        MimirDbContext db, Guid wisdomId, WisdomCandidate candidate, CancellationToken cancellationToken)
    {
        var recorded = await LinksInDbAsync(db, wisdomId, cancellationToken);
        foreach (var link in LinksOf(candidate).Except(recorded))
        {
            db.Provenance.Add(NewProvenance(wisdomId, link));
        }
    }

    private static async Task<List<ProvenanceLink>> LinksInDbAsync(
        MimirDbContext db, Guid wisdomId, CancellationToken cancellationToken)
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
