using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Recall;

internal sealed class McpProjects(MimirDbContext db)
{
    public async Task<Project?> FindRequesterAsync(
        string identity, string rootPath, CancellationToken cancellationToken)
        => await db.Projects.FirstOrDefaultAsync(p => p.Identity == identity, cancellationToken)
            ?? await db.Projects.FirstOrDefaultAsync(
                p => p.RootPaths.Contains(rootPath), cancellationToken);

    public async Task<(Project? Match, string? Miss)> ResolveFilterAsync(
        string? filter, CancellationToken cancellationToken)
    {
        if (filter is not { Length: > 0 })
        {
            return (null, null);
        }

        var lowered = filter.ToLowerInvariant();
        var match = await db.Projects
            .Where(p => p.DisplayName.ToLower() == lowered || p.Identity.ToLower() == lowered)
            .OrderBy(p => p.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (match is not null)
        {
            return (match, null);
        }

        var known = await db.Projects
            .OrderBy(p => p.DisplayName)
            .Select(p => p.DisplayName)
            .Take(50)
            .ToListAsync(cancellationToken);
        return (null, $"No project matches '{filter}'. Known projects: {string.Join(", ", known)}.");
    }

    public async Task<IReadOnlyDictionary<Guid, string>> DisplayNamesAsync(
        IEnumerable<Guid> projectIds, CancellationToken cancellationToken)
    {
        var ids = projectIds.Distinct().ToList();
        return await db.Projects
            .Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.DisplayName, cancellationToken);
    }
}
