using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Recall;

/// <summary>
/// The one way an Injection row is recorded (§3): what a lane actually put in front of a session,
/// when, and how much. Shared by both ambient lanes so the row's shape cannot drift between them;
/// callers only record actual injections — empty decisions leave no trace (§7).
/// </summary>
internal static class InjectionLog
{
    public static void Record(
        MimirDbContext db,
        string sessionId,
        Guid projectId,
        DateTimeOffset at,
        InjectionLane lane,
        string? queryContext,
        string injection,
        IReadOnlyList<InjectionEntry> included)
        => db.Injections.Add(new Injection
        {
            Id = Guid.CreateVersion7(),
            SessionId = sessionId,
            ProjectId = projectId,
            At = at,
            Lane = lane,
            QueryContext = queryContext,
            Chars = injection.Length,
            Items = included
                .Select(e => new InjectionItem { WisdomId = e.WisdomId, Score = e.Score })
                .ToList(),
        });
}
