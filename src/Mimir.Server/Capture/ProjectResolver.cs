using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Capture;

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
                    try
                    {
                        await ProjectMerger.MergeAsync(db, survivorId: project.Id, loserId: rival.Id, cancellationToken);
                    }
                    catch (PostgresException ex) when (
                        ex.IsForeignKeyViolation() && attempt < DbRaces.CreateRaceMaxAttempts)
                    {
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
                db.Entry(project).State = EntityState.Detached;
            }
        }
    }

    /// <summary>Path-born in memory, not in the query: EF cannot translate array-contains-own-column.</summary>
    private async Task<Project?> PathBornRivalAtAsync(
        Project project, string rootPath, CancellationToken cancellationToken)
    {
        var holders = await db.Projects
            .Where(p => p.Id != project.Id && p.RootPaths.Contains(rootPath))
            .ToListAsync(cancellationToken);
        return holders.FirstOrDefault(r => r.IsPathBorn);
    }

    private static bool ReportsARemoteFor(Project project, string identity, string rootPath)
        => project.Identity != identity
            && identity != rootPath
            && project.IsPathBorn;

    private static string DisplayNameOf(string identity)
    {
        var name = identity.TrimEnd('/', '\\').Split('/', '\\')[^1];
        return name.Length > 0 ? name : identity;
    }
}
