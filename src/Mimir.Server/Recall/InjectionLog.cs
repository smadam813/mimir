using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Recall;

internal sealed record InjectionEntry(
    Guid WisdomId,
    double Score,
    WisdomKind Kind,
    bool IsGlobal,
    DateTimeOffset LastConfirmedAt,
    string Text);

internal sealed record InjectionContext(
    InjectionLane Lane,
    string SessionId,
    Guid ProjectId,
    string? QueryContext);

internal sealed class InjectionLog(MimirDbContext db, TimeProvider clock)
{
    public async Task<string> RenderAndRecordAsync(
        InjectionContext context,
        IEnumerable<InjectionEntry> entries,
        int budgetChars,
        string? notice,
        CancellationToken cancellationToken)
    {
        var (text, included) = InjectionWrapper.Build(entries, budgetChars, notice);

        if (included.Count > 0)
        {
            await AddAsync(context, text, included, cancellationToken);
        }

        return text;
    }

    public async Task RecordAsync(
        InjectionContext context,
        string text,
        IReadOnlyList<InjectionEntry> included,
        CancellationToken cancellationToken)
    {
        if (text.Length == 0)
        {
            return;
        }

        await AddAsync(context, text, included, cancellationToken);
    }

    private async Task AddAsync(
        InjectionContext context,
        string text,
        IReadOnlyList<InjectionEntry> included,
        CancellationToken cancellationToken)
    {
        db.Injections.Add(new Injection
        {
            Id = Guid.CreateVersion7(),
            SessionId = context.SessionId,
            ProjectId = context.ProjectId,
            At = clock.GetUtcNow(),
            Lane = context.Lane,
            QueryContext = context.QueryContext,
            Chars = text.Length,
            Items = included
                .Select(e => new InjectionItem { WisdomId = e.WisdomId, Score = e.Score })
                .ToList(),
        });

        await db.SaveChangesAsync(cancellationToken);
    }
}
