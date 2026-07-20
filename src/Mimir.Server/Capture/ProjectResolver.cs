using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Capture;

/// <summary>
/// Spec §3.1 server side: match by identity, else by a root this Project has been seen at,
/// creating one if needed and appending unseen roots. A path-identity Project that later reports
/// a remote identity is upgraded in place — same row, id stable.
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
                    // Appended in the database, not in memory: a tracked save would overwrite a
                    // concurrent hook's append with this context's stale array. The WHERE guard
                    // re-checks under the row lock (EF cannot translate an array append into
                    // ExecuteUpdate, hence SQL); the reload brings the merged array back.
                    await db.Database.ExecuteSqlAsync(
                        $"""
                        UPDATE projects
                        SET root_paths = array_append(root_paths, {rootPath})
                        WHERE id = {project.Id} AND NOT ({rootPath} = ANY (root_paths))
                        """,
                        cancellationToken);
                    await db.Entry(project).ReloadAsync(cancellationToken);
                }

                if (project.Identity == identity && identity != rootPath
                    && await PathBornRivalAtAsync(project, rootPath, cancellationToken) is { } rival)
                {
                    // Clone merge (§3.1): this remote identity already has its Project, yet the
                    // reported root belongs to a path-born one — two clones of one repository.
                    try
                    {
                        await ProjectMerger.MergeAsync(db, survivorId: project.Id, loserId: rival.Id, cancellationToken);
                    }
                    catch (PostgresException ex) when (
                        ex.IsForeignKeyViolation() && attempt < DbRaces.CreateRaceMaxAttempts)
                    {
                        // A concurrent hook referenced the loser between re-point and delete; the
                        // transaction rolled back whole. Re-read and merge again.
                        db.ChangeTracker.Clear();
                        continue;
                    }

                    db.Entry(rival).State = EntityState.Detached;
                    await db.Entry(project).ReloadAsync(cancellationToken);
                }
                else if (ReportsARemoteFor(project, identity, rootPath))
                {
                    try
                    {
                        // Identity upgrade (§3.1): the row was matched by root, its stored identity
                        // is the path it was born at, and the hook knows the real remote. The WHERE
                        // guard makes first-upgrade-wins atomic; a rival upgrading to the same
                        // remote leaves nothing to do, and the reload is truthful either way.
                        await db.Database.ExecuteSqlAsync(
                            $"""
                            UPDATE projects
                            SET identity = {identity}, display_name = {DisplayNameOf(identity)}
                            WHERE id = {project.Id} AND identity = {project.Identity}
                            """,
                            cancellationToken);
                        await db.Entry(project).ReloadAsync(cancellationToken);
                    }
                    catch (PostgresException ex) when (
                        ex.IsUniqueViolation() && attempt < DbRaces.CreateRaceMaxAttempts)
                    {
                        // The remote identity already names another Project: two clones of one
                        // repository. Re-read; the identity match finds the survivor.
                        db.Entry(project).State = EntityState.Detached;
                        continue;
                    }
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
            catch (DbUpdateException ex) when (ex.IsUniqueViolation() && attempt < DbRaces.CreateRaceMaxAttempts)
            {
                // Lost a create race on the unique identity: forget ours, match the winner.
                db.Entry(project).State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// A different, path-born Project claiming the reported root — the loser of a clone merge.
    /// Path-born is checked in memory, not in the query: at most one other row holds this root,
    /// and the array-contains-own-column shape has no reliable translation.
    /// </summary>
    private async Task<Project?> PathBornRivalAtAsync(
        Project project, string rootPath, CancellationToken cancellationToken)
    {
        var rival = await db.Projects.FirstOrDefaultAsync(
            p => p.Id != project.Id && p.RootPaths.Contains(rootPath), cancellationToken);
        return rival is not null && rival.RootPaths.Contains(rival.Identity) ? rival : null;
    }

    /// <summary>
    /// True when the hook reports a remote identity for a Project that never had one. A path-born
    /// Project carries the root it was created at as identity, so its identity sits in its own
    /// <c>root_paths</c>; a remote identity never does (roots come from the filesystem, identities
    /// from <c>RemoteIdentity.Normalize</c>). An incoming identity equal to the reported root is
    /// the §3.1 fallback — a hook that still knows no remote upgrades nothing.
    /// </summary>
    private static bool ReportsARemoteFor(Project project, string identity, string rootPath)
        => project.Identity != identity
            && identity != rootPath
            && project.RootPaths.Contains(project.Identity);

    /// <summary>The last segment of either identity form: <c>host/owner/repo</c> or a path.</summary>
    private static string DisplayNameOf(string identity)
    {
        var name = identity.TrimEnd('/', '\\').Split('/', '\\')[^1];
        return name.Length > 0 ? name : identity;
    }
}
