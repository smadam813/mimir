using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Pgvector;

namespace Mimir.Server.Distillation;

internal sealed record WisdomCandidate(
    WisdomKind Kind,
    Guid ScopeProjectId,
    string Text,
    Guid? HarvestedItemId = null,
    Guid? EpisodeId = null,
    IReadOnlyList<Guid>? EventIds = null);

internal enum WisdomEditNoOp
{
    Blank,
    Unknown,
    Unchanged,
}

internal sealed class MergeGate(
    IDbContextFactory<MimirDbContext> contexts,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    IOptions<SearchOptions> searchOptions,
    IMergeArbiter arbiter,
    IOptions<DistillationOptions> options,
    TimeProvider clock)
{
    /// <summary>0x6D696D6972 is "mimir" in ASCII.</summary>
    internal const long AdmissionLockKey = 0x6D696D6972;

    public async Task AdmitAllAsync(
        IReadOnlyList<WisdomCandidate> candidates,
        Func<MimirDbContext, CancellationToken, Task>? finalizer,
        CancellationToken cancellationToken)
    {
        var vectors = candidates.Count == 0
            ? []
            : (await embeddings.GenerateAsync(
                    candidates.Select(c => c.Text), cancellationToken: cancellationToken))
                .Select(e => new Vector(e.Vector))
                .ToList();

        if (vectors.Count != candidates.Count)
        {
            throw new InvalidOperationException(
                $"the embedding batch returned {vectors.Count} vector(s) for {candidates.Count} candidate(s)");
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var search = new WisdomSearch(db, searchOptions);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (candidates.Count > 0)
        {
            await TakeAdmissionLockAsync(db, cancellationToken);
        }

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

    public async Task EditAsync(Guid wisdomId, string text, CancellationToken cancellationToken)
    {
        var trimmed = text.Trim();

        // current: null so that only the Blank arm can fire here.
        if (NoOpOf(trimmed, current: null) is WisdomEditNoOp.Blank)
        {
            return;
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        var current = await db.Wisdom.AsNoTracking()
            .Where(w => w.Id == wisdomId)
            .Select(w => w.Text)
            .FirstOrDefaultAsync(cancellationToken);
        if (NoOpOf(trimmed, current) is not null)
        {
            return;
        }

        var embedding = await EmbedAsync(trimmed, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await TakeAdmissionLockAsync(db, cancellationToken);

        var wisdom = await db.Wisdom.FirstOrDefaultAsync(w => w.Id == wisdomId, cancellationToken);
        // The null arm is the compiler's, not the rule's: RewriteAsync takes a non-null row.
        if (wisdom is null || NoOpOf(trimmed, wisdom.Text) is not null)
        {
            return;
        }

        await RewriteAsync(db, wisdom, trimmed, embedding, WisdomVersionCause.Edited, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    internal static WisdomEditNoOp? NoOpOf(string text, string? current) => text.Trim() switch
    {
        { Length: 0 } => WisdomEditNoOp.Blank,
        _ when current is null => WisdomEditNoOp.Unknown,
        var trimmed when trimmed == current => WisdomEditNoOp.Unchanged,
        _ => null,
    };

    private static async Task TakeAdmissionLockAsync(MimirDbContext db, CancellationToken cancellationToken)
        => await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({AdmissionLockKey})", cancellationToken);

    private async Task AdmitAsync(
        MimirDbContext db,
        WisdomSearch search,
        WisdomCandidate candidate,
        Vector embedding,
        CancellationToken cancellationToken)
    {
        var hits = await search.SearchAsync(embedding, candidate.Text, cancellationToken);

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
                    Supersede(db, matched, candidate, embedding);
                    break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool ProjectInPlay(Wisdom matched, WisdomCandidate candidate)
        => matched.ScopeProjectId != Project.GlobalId || candidate.ScopeProjectId != Project.GlobalId;

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

    private async Task MergeAsync(
        MimirDbContext db,
        Wisdom wisdom,
        WisdomCandidate candidate,
        string mergedText,
        CancellationToken cancellationToken)
    {
        wisdom.Reinforcement++;
        wisdom.LastConfirmedAt = clock.GetUtcNow();

        if (wisdom.ScopeProjectId != Project.GlobalId
            && candidate.ScopeProjectId != Project.GlobalId
            && candidate.ScopeProjectId != wisdom.ScopeProjectId)
        {
            wisdom.ScopeProjectId = Project.GlobalId;
        }

        await UnionProvenanceAsync(db, wisdom.Id, candidate, cancellationToken);
        if (mergedText != wisdom.Text)
        {
            await RewriteAsync(
                db,
                wisdom,
                mergedText,
                await EmbedAsync(mergedText, cancellationToken),
                WisdomVersionCause.Merged,
                cancellationToken);
        }
    }

    private void Supersede(
        MimirDbContext db, Wisdom wisdom, WisdomCandidate candidate, Vector embedding)
    {
        var successor = Insert(db, candidate, embedding, WisdomVersionCause.Adjudicated);
        successor.ContestedAt = successor.LastConfirmedAt;
        wisdom.SupersededBy = successor.Id;
        wisdom.RetiredAt = successor.LastConfirmedAt;
    }

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
