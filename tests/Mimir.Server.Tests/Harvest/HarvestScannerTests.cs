using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mimir.Server.Capture;
using Mimir.Server.Configuration;
using Mimir.Server.Harvest;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Capture;

namespace Mimir.Server.Tests.Harvest;

/// <summary>
/// Spec §5 against a real Postgres and a real directory tree: memory files become HarvestedItems
/// under the right Projects, edits re-version with priors kept, deletions set <c>gone_at</c>. The
/// first scan of an empty database is the Backfill — there is no special mode to test separately.
/// </summary>
public sealed class HarvestScannerTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);

    private string _root = "";
    private MimirDbContext? _context;

    public async ValueTask InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("mimir-harvest-").FullName;

        // The class shares one database while every test scans a fresh root, so a leftover item
        // from an earlier test would be "gone" in the next test's scan and pollute its counts.
        // Production has no such seam — one root, one database.
        if (fixture.UnavailableReason is null)
        {
            await using var db = fixture.CreateContext();
            await db.HarvestedItems.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Directory.Delete(_root, recursive: true);
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheBackfill_StoresEveryMemoryFileUnderItsProject()
    {
        var (project, rootPath) = await SeedProjectAsync();
        var slug = MemorySlug.Mangle(rootPath);
        WriteMemoryFile(slug, "MEMORY.md", "# Memory Index\n- facts");
        WriteMemoryFile(slug, "mimir-map.md", "the map");

        var result = await Scanner().ScanAsync(Token);

        result.Items.ShouldBe(2);
        result.Changed.ShouldBe(2);
        result.Gone.ShouldBe(0);

        var items = await FromDb(db => db.HarvestedItems
            .Where(i => i.ProjectId == project.Id).OrderBy(i => i.Path).ToListAsync(Token));
        items.Count.ShouldBe(2);
        items[0].Path.ShouldBe($"{slug}/memory/MEMORY.md");
        items[0].Content.ShouldBe("# Memory Index\n- facts");
        items[0].FirstSeen.ShouldBe(Now);
        items[0].LastChanged.ShouldBe(Now);
        items[0].GoneAt.ShouldBeNull();
    }

    [Fact]
    public async Task AHyphenatedRoot_ResolvesByRemanglingKnownRoots_NotByGuessingThePath()
    {
        // `C--git-fh6-tuning-…` could demangle to `C:\git\fh6\tuning\…`; the Project whose known
        // root mangles to the slug must win over that guess (§5 slug mapping).
        var (project, rootPath) = await SeedProjectAsync("fh6-tuning-calculator");
        WriteMemoryFile(MemorySlug.Mangle(rootPath), "MEMORY.md", "hyphens intact");

        await Scanner().ScanAsync(Token);

        var item = await FromDb(db => db.HarvestedItems
            .SingleAsync(i => i.ProjectId == project.Id, Token));
        item.Content.ShouldBe("hyphens intact");
        (await FromDb(db => db.Projects.CountAsync(
            p => p.RootPaths.Any(r => r.Contains("fh6")), Token))).ShouldBe(1);
    }

    [Fact]
    public async Task AnUnknownSlug_CreatesAPathIdentityProjectForItsDemangledRoot()
    {
        var slug = NewSlug();
        var demangled = MemorySlug.Demangle(slug);
        WriteMemoryFile(slug, "MEMORY.md", "fresh");

        await Scanner().ScanAsync(Token);

        var project = await FromDb(db => db.Projects.SingleAsync(p => p.Identity == demangled, Token));
        project.RootPaths.ShouldBe([demangled]);
        var item = await FromDb(db => db.HarvestedItems.SingleAsync(i => i.ProjectId == project.Id, Token));
        item.Path.ShouldBe($"{slug}/memory/MEMORY.md");
    }

    [Fact]
    public async Task AnUnchangedFile_GetsNoNewVersion()
    {
        var slug = NewSlug();
        WriteMemoryFile(slug, "MEMORY.md", "stable");
        await Scanner().ScanAsync(Token);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var second = await Scanner().ScanAsync(Token);

        second.Items.ShouldBe(1);
        second.Changed.ShouldBe(0);
        (await CountVersionsAsync(slug)).ShouldBe(1);
    }

    [Fact]
    public async Task AnEditedFile_GetsANewVersionAndThePriorIsKept()
    {
        var slug = NewSlug();
        WriteMemoryFile(slug, "MEMORY.md", "first insight");
        await Scanner().ScanAsync(Token);
        _clock.Advance(TimeSpan.FromMinutes(5));
        WriteMemoryFile(slug, "MEMORY.md", "sharper insight");

        var result = await Scanner().ScanAsync(Token);

        result.Changed.ShouldBe(1);
        var versions = await VersionsAsync(slug);
        versions.Count.ShouldBe(2);
        versions[0].Content.ShouldBe("first insight");
        versions[1].Content.ShouldBe("sharper insight");
        versions[1].ContentHash.ShouldNotBe(versions[0].ContentHash);
        versions[1].FirstSeen.ShouldBe(Now, "first_seen follows the path, not the version");
        versions[1].LastChanged.ShouldBe(Now.AddMinutes(5));
    }

    [Fact]
    public async Task ADeletedFile_IsMarkedGoneNotRemoved()
    {
        var slug = NewSlug();
        WriteMemoryFile(slug, "MEMORY.md", "ephemeral");
        await Scanner().ScanAsync(Token);
        _clock.Advance(TimeSpan.FromMinutes(5));
        File.Delete(Path.Combine(_root, slug, "memory", "MEMORY.md"));

        var result = await Scanner().ScanAsync(Token);

        result.Items.ShouldBe(0);
        result.Gone.ShouldBe(1);
        var version = (await VersionsAsync(slug)).ShouldHaveSingleItem();
        version.GoneAt.ShouldBe(Now.AddMinutes(5));
        version.Content.ShouldBe("ephemeral");
    }

    [Fact]
    public async Task AReappearedFile_IsAliveAgainAndTheGoneVersionStaysInHistory()
    {
        var slug = NewSlug();
        WriteMemoryFile(slug, "MEMORY.md", "resilient");
        await Scanner().ScanAsync(Token);
        _clock.Advance(TimeSpan.FromMinutes(5));
        File.Delete(Path.Combine(_root, slug, "memory", "MEMORY.md"));
        await Scanner().ScanAsync(Token);
        _clock.Advance(TimeSpan.FromMinutes(5));
        WriteMemoryFile(slug, "MEMORY.md", "resilient");

        await Scanner().ScanAsync(Token);

        var versions = await VersionsAsync(slug);
        versions.Count.ShouldBe(2);
        versions[0].GoneAt.ShouldBe(Now.AddMinutes(5));
        versions[1].GoneAt.ShouldBeNull();
        versions[1].FirstSeen.ShouldBe(Now);
    }

    [Fact]
    public async Task OnlyMarkdownUnderAMemoryDirectoryIsHarvested()
    {
        var slug = NewSlug();
        WriteMemoryFile(slug, "MEMORY.md", "yes");
        WriteMemoryFile(slug, Path.Combine("deep", "nested.md"), "yes, recursively");
        WriteFile(Path.Combine(_root, slug, "memory", "notes.txt"), "not markdown");
        WriteFile(Path.Combine(_root, slug, "session.jsonl"), "not memory");
        WriteFile(Path.Combine(_root, "stray.md"), "not in a project");

        var result = await Scanner().ScanAsync(Token);

        result.Items.ShouldBe(2);
        var versions = await VersionsAsync(slug);
        versions.Select(v => v.Path).ShouldBe(
            [$"{slug}/memory/MEMORY.md", $"{slug}/memory/deep/nested.md"], ignoreOrder: true);
    }

    [Fact]
    public async Task AnUnreadableFile_KeepsItsStateInsteadOfGoingGone()
    {
        // A memory file locked mid-write is present, just briefly unreadable. Marking it gone
        // would fabricate a deletion — and resurrect it as a spurious new version next scan.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows enforces FileShare.None.");
        var slug = NewSlug();
        WriteMemoryFile(slug, "MEMORY.md", "locked later");
        await Scanner().ScanAsync(Token);
        _clock.Advance(TimeSpan.FromMinutes(5));

        HarvestScanResult result;
        using (File.Open(
            Path.Combine(_root, slug, "memory", "MEMORY.md"),
            FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await Scanner().ScanAsync(Token);
        }

        result.Items.ShouldBe(1, "a locked file was still seen");
        result.Gone.ShouldBe(0);
        var version = (await VersionsAsync(slug)).ShouldHaveSingleItem();
        version.GoneAt.ShouldBeNull();
    }

    [Fact]
    public async Task AMissingHarvestRoot_ThrowsInsteadOfMarkingEverythingGone()
    {
        var slug = NewSlug();
        WriteMemoryFile(slug, "MEMORY.md", "still here");
        await Scanner().ScanAsync(Token);
        Directory.Delete(_root, recursive: true);

        await Should.ThrowAsync<DirectoryNotFoundException>(() => Scanner().ScanAsync(Token));

        Directory.CreateDirectory(_root); // so DisposeAsync still has something to delete
        (await VersionsAsync(slug)).ShouldAllBe(v => v.GoneAt == null);
    }

    private HarvestScanner Scanner()
        => new(
            Context,
            new ProjectResolver(Context),
            Options.Create(new HarvestOptions { Root = _root }),
            _clock,
            NullLogger<HarvestScanner>.Instance);

    /// <summary>A Project already known from hook traffic, at a unique (hyphenated) root.</summary>
    private async Task<(Project Project, string RootPath)> SeedProjectAsync(string name = "proj")
    {
        var suffix = Guid.NewGuid().ToString("N");
        var rootPath = $@"C:\git\{name}-{suffix}";
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            Identity = $"github.com/test/{name}-{suffix}",
            RootPaths = [rootPath],
            DisplayName = name,
        };

        await using var db = fixture.CreateContext();
        db.Projects.Add(project);
        await db.SaveChangesAsync(Token);
        return (project, rootPath);
    }

    /// <summary>A slug unique per test: the database outlives each test's harvest root.</summary>
    private static string NewSlug() => $"C--git-harvest-{Guid.NewGuid():N}";

    private void WriteMemoryFile(string slug, string relativePath, string content)
        => WriteFile(Path.Combine(_root, slug, "memory", relativePath), content);

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task<List<HarvestedItem>> VersionsAsync(string slug)
        => await FromDb(db => db.HarvestedItems
            .Where(i => i.Path.StartsWith(slug))
            .OrderBy(i => i.LastChanged).ThenBy(i => i.Id)
            .ToListAsync(Token));

    private async Task<int> CountVersionsAsync(string slug)
        => (await VersionsAsync(slug)).Count;

    private async Task<T> FromDb<T>(Func<MimirDbContext, Task<T>> query)
    {
        await using var context = fixture.CreateContext();
        return await query(context);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private MimirDbContext Context
    {
        get
        {
            if (fixture.UnavailableReason is { } reason)
            {
                Assert.Skip(TestPostgres.SkipMessage(reason));
            }

            return _context ??= fixture.CreateContext();
        }
    }
}
