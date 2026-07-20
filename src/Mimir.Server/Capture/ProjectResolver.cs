using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Capture;

/// <summary>
/// Spec §3.1 server side: match by identity, else by a root this Project has been seen at,
/// creating one if needed and appending unseen roots. A root match keeps its stored identity —
/// identity upgrade and clone merge are a follow-up ticket.
/// </summary>
internal sealed class ProjectResolver(MimirDbContext db)
{
    public async Task<Project> ResolveAsync(string identity, string rootPath, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Identity == identity, cancellationToken)
                ?? await db.Projects.FirstOrDefaultAsync(p => p.RootPaths.Contains(rootPath), cancellationToken);

            if (project is not null)
            {
                if (!project.RootPaths.Contains(rootPath))
                {
                    project.RootPaths = [.. project.RootPaths, rootPath];
                    await db.SaveChangesAsync(cancellationToken);
                }

                return project;
            }

            project = new Project
            {
                Id = Guid.CreateVersion7(),
                Identity = identity,
                RootPaths = [rootPath],
                DisplayName = DisplayNameOf(identity),
            };
            db.Projects.Add(project);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return project;
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                // Lost a create race on the unique identity: forget ours, match the winner.
                db.Entry(project).State = EntityState.Detached;
            }
        }
    }

    /// <summary>The last segment of either identity form: <c>host/owner/repo</c> or a path.</summary>
    private static string DisplayNameOf(string identity)
    {
        var name = identity.TrimEnd('/', '\\').Split('/', '\\')[^1];
        return name.Length > 0 ? name : identity;
    }
}
