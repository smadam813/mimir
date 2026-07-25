using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Recall;

/// <summary>One Wisdom bound for injection: the score that ordered it plus what its label needs.</summary>
internal sealed record InjectionEntry(
    Guid WisdomId,
    double Score,
    WisdomKind Kind,
    bool IsGlobal,
    DateTimeOffset LastConfirmedAt,
    string Text);

/// <summary>
/// The §3 columns saying whose injection this is: which lane, in which session, recorded against
/// which Project, behind which query. Travels as one because an Injection row is unidentifiable
/// without all four.
/// </summary>
/// <param name="QueryContext">The prompt for Prompt, the tool query for MCP, null for the Brief (§3).</param>
/// <param name="ProjectId">The lane's own Project, and deliberately not one meaning shared by the
/// three. Brief and Prompt pass the session's Project — the one whose ambient universe was drawn
/// from. MCP passes the affinity Project: the requester's cwd Project, or Global when the directory
/// matches none, which is the context its ranking boosted under and not necessarily any Project in
/// the answer. Each value is honest for its lane; the column is not a single "where this came
/// from".</param>
internal sealed record InjectionContext(
    InjectionLane Lane,
    string SessionId,
    Guid ProjectId,
    string? QueryContext);

/// <summary>
/// The one keeper of the §7 recording rules and the only writer of an Injection row (§3): what a
/// lane actually put in front of a session, when, and how much. A lane hands over its decision and
/// is done — the empty-trace rule, the clock the row is stamped from, and the save all live here,
/// so no lane can state any of the three differently from the others.
///
/// **The empty-trace rule (§7) has two shapes, one per surface.** The ambient lanes render through
/// here, and nothing included means no row: an empty decision leaves no trace. The rendered text
/// still comes back, because a lane may have raised a notice that has to go out with no Wisdom
/// behind it — the Brief's growth tripwire says "this compose was degraded", and a compose that
/// swallowed the line along with the last entry would be silent in exactly the case it was raised
/// for.
/// <c>mimir_search</c> composes its own sectioned answer and records through
/// <see cref="RecordAsync"/>, where the rule reads off the answer instead — an answer that carried
/// results is a real injection even when every one of them was an Episode and no Wisdom rode along.
///
/// What that lane's replies to a query it could not run — an unknown kind, an unresolvable Project
/// filter, no matches at all — do is its own business: they never reach here, because nothing was
/// recalled to record. The guard on <see cref="RecordAsync"/> is this keeper's floor under any
/// caller, not a restatement of that decision.
/// </summary>
internal sealed class InjectionLog(MimirDbContext db, TimeProvider clock)
{
    /// <summary>
    /// The ambient lanes' whole tail: builds the §7 <see cref="InjectionWrapper"/>, records what it
    /// injected, and hands back the text to inject.
    /// </summary>
    /// <param name="entries">Candidates in injection order (highest score first).</param>
    /// <param name="budgetChars">The lane's budget for the whole rendered wrapper (§11).</param>
    /// <param name="notice">A trailing non-Wisdom line, or null for none — reserved out of the
    /// budget before any entry is measured (<see cref="InjectionWrapper.Build"/>).</param>
    /// <returns>The injection text — "" when nothing was rendered at all.</returns>
    public async Task<string> RenderAndRecordAsync(
        InjectionContext context,
        IEnumerable<InjectionEntry> entries,
        int budgetChars,
        string? notice,
        CancellationToken cancellationToken)
    {
        var (text, included) = InjectionWrapper.Build(entries, budgetChars, notice);

        // §7: an empty decision leaves no trace. Read off what was included, never off the text —
        // a notice with no Wisdom behind it renders, and is not an injection.
        if (included.Count > 0)
        {
            await AddAsync(context, text, included, cancellationToken);
        }

        return text;
    }

    /// <summary>
    /// Records an answer a lane composed itself — <c>mimir_search</c>, whose sectioned reply is not
    /// the ambient wrapper and whose budget is a count cap rather than chars.
    /// </summary>
    /// <param name="text">The composed answer, exactly as the caller will return it.</param>
    /// <param name="included">The Wisdom the answer rendered, in rank order — empty for an answer
    /// that found only Episodes, which is still an injection.</param>
    public async Task RecordAsync(
        InjectionContext context,
        string text,
        IReadOnlyList<InjectionEntry> included,
        CancellationToken cancellationToken)
    {
        // §7: nothing delivered, nothing recorded. Here the answer is the whole of what was
        // delivered — there is no wrapper it could have rendered without — so emptiness is read
        // off the text.
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

        // The shared scoped context, as every lane's own save was before: recall stages nothing
        // else on it by the time it reaches here, so this commits the injection row and nothing
        // more. A lane that grows work of its own to stage would have to move off it.
        await db.SaveChangesAsync(cancellationToken);
    }
}
