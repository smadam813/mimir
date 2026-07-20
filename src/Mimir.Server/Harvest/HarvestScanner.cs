using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Capture;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Harvest;

/// <summary>What one scan did, feeding the Harvester tile (§8).</summary>
/// <param name="Items">Memory files found this scan.</param>
/// <param name="Changed">Files that stored a new HarvestedItem version.</param>
/// <param name="Gone">Files newly found deleted.</param>
internal sealed record HarvestScanResult(int Items, int Changed, int Gone);

/// <summary>
/// One §5 scan over the harvest root: every <c>&lt;slug&gt;/memory/**/*.md</c> is content-hashed
/// and stored as a HarvestedItem version when new or changed; files no longer on disk get
/// <c>gone_at</c>. The first scan of an empty database is the Backfill — no special mode.
/// </summary>
internal sealed class HarvestScanner(
    MimirDbContext db,
    ProjectResolver projects,
    IOptions<HarvestOptions> options,
    TimeProvider clock,
    ILogger<HarvestScanner> logger)
{
    public async Task<HarvestScanResult> ScanAsync(CancellationToken cancellationToken)
    {
        var root = options.Value.Root;
        if (!Directory.Exists(root))
        {
            // Refusing to scan, rather than seeing zero files, is what keeps a broken mount from
            // marking every item gone.
            throw new DirectoryNotFoundException($"Harvest root '{root}' does not exist.");
        }

        var now = clock.GetUtcNow();
        var latestByPath = await LatestVersionsAsync(cancellationToken);
        var resolver = new SlugProjectResolver(db, projects);

        var scannedPaths = new HashSet<string>(StringComparer.Ordinal);
        var items = 0;
        var changed = 0;

        foreach (var (slug, file) in MemoryFilesUnder(root))
        {
            var path = ItemPathOf(root, file);
            items++;
            scannedPaths.Add(path);

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(file, cancellationToken);
            }
            catch (IOException ex)
            {
                // The host is live under us; a file locked or vanishing mid-scan is next scan's
                // problem, not this scan's failure. It was seen, so the gone-marking below leaves
                // it alone: it keeps whatever state it had.
                logger.LogWarning(ex, "Skipping unreadable memory file {File}", file);
                continue;
            }

            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var prior = latestByPath.GetValueOrDefault(path);
            if (prior is not null && prior.ContentHash == hash && prior.GoneAt is null)
            {
                continue;
            }

            db.HarvestedItems.Add(new HarvestedItem
            {
                Id = Guid.CreateVersion7(),
                ProjectId = await resolver.ResolveAsync(slug, cancellationToken),
                Path = path,
                ContentHash = hash,
                Content = Encoding.UTF8.GetString(bytes),
                FirstSeen = prior?.FirstSeen ?? now,
                LastChanged = now,
            });
            changed++;
        }

        await db.SaveChangesAsync(cancellationToken);

        var goneIds = latestByPath.Values
            .Where(v => v.GoneAt is null && !scannedPaths.Contains(v.Path))
            .Select(v => v.Id)
            .ToArray();
        var gone = goneIds.Length == 0
            ? 0
            : await db.HarvestedItems
                .Where(i => goneIds.Contains(i.Id))
                .ExecuteUpdateAsync(update => update.SetProperty(i => i.GoneAt, now), cancellationToken);

        return new HarvestScanResult(items, changed, gone);
    }

    /// <summary>
    /// The current state of every path ever harvested: its latest version's hash and liveness.
    /// Loaded whole — the version count grows at memory-file edit pace, and no Content comes
    /// along — then reduced in memory, because "latest row per group" does not translate to SQL
    /// EF Core reliably supports.
    /// </summary>
    private async Task<Dictionary<string, LatestVersion>> LatestVersionsAsync(
        CancellationToken cancellationToken)
    {
        var versions = await db.HarvestedItems
            .Select(i => new LatestVersion(i.Id, i.Path, i.ContentHash, i.FirstSeen, i.LastChanged, i.GoneAt))
            .ToListAsync(cancellationToken);

        return versions
            .GroupBy(v => v.Path, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(v => v.LastChanged).ThenByDescending(v => v.Id).First(),
                StringComparer.Ordinal);
    }

    private static IEnumerable<(string Slug, string File)> MemoryFilesUnder(string root)
    {
        foreach (var slugDirectory in Directory.EnumerateDirectories(root))
        {
            var memoryDirectory = Path.Combine(slugDirectory, "memory");
            if (!Directory.Exists(memoryDirectory))
            {
                continue;
            }

            var slug = Path.GetFileName(slugDirectory);
            foreach (var file in Directory.EnumerateFiles(memoryDirectory, "*.md", SearchOption.AllDirectories))
            {
                yield return (slug, file);
            }
        }
    }

    /// <summary>Harvest-relative with forward slashes: the same identity whatever the mount.</summary>
    private static string ItemPathOf(string root, string file)
        => Path.GetRelativePath(root, file).Replace('\\', '/');

    private sealed record LatestVersion(
        Guid Id,
        string Path,
        string ContentHash,
        DateTimeOffset FirstSeen,
        DateTimeOffset LastChanged,
        DateTimeOffset? GoneAt);

    /// <summary>
    /// §5 slug → Project, memoized per scan. Mangling a known root is exact (hyphens and all), so
    /// that wins; only an unmatched slug falls back to the demangled guess, creating the
    /// path-identity Project that hook traffic upgrades or merges later.
    /// </summary>
    private sealed class SlugProjectResolver(MimirDbContext db, ProjectResolver projects)
    {
        private readonly Dictionary<string, Guid> _bySlug = new(StringComparer.Ordinal);

        private List<KnownProject>? _known;

        public async Task<Guid> ResolveAsync(string slug, CancellationToken cancellationToken)
        {
            if (_bySlug.TryGetValue(slug, out var cached))
            {
                return cached;
            }

            _known ??= await db.Projects
                .Select(p => new KnownProject(p.Id, p.RootPaths))
                .ToListAsync(cancellationToken);

            var match = _known.FirstOrDefault(
                p => p.RootPaths.Any(rootPath => MemorySlug.MatchesRoot(slug, rootPath)));
            var projectId = match?.Id;
            if (projectId is null)
            {
                var rootPath = MemorySlug.Demangle(slug);
                var project = await projects.ResolveAsync(rootPath, rootPath, cancellationToken);
                projectId = project.Id;
            }

            _bySlug[slug] = projectId.Value;
            return projectId.Value;
        }

        private sealed record KnownProject(Guid Id, string[] RootPaths);
    }
}
