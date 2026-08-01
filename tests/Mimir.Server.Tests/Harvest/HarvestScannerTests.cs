using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mimir.Server.Capture;
using Mimir.Server.Configuration;
using Mimir.Server.Harvest;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Harvest;

public sealed class HarvestScannerTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    private const string Slug = "C--git-harvest";

    private string _root = "";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _root = Directory.CreateTempSubdirectory("mimir-harvest-").FullName;
    }

    public override async ValueTask DisposeAsync()
    {
        Directory.Delete(_root, recursive: true);
        await base.DisposeAsync();
    }

    [Fact]
    public async Task TheBackfill_StoresEveryMemoryFileUnderItsProject()
    {
        var project = await AddProjectAsync();
        var slug = MemorySlug.Mangle(project.RootPaths[0]);
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
        var project = await AddProjectAsync("fh6-tuning-calculator");
        WriteMemoryFile(MemorySlug.Mangle(project.RootPaths[0]), "MEMORY.md", "hyphens intact");

        await Scanner().ScanAsync(Token);

        var item = await FromDb(db => db.HarvestedItems.SingleAsync(Token));
        item.ProjectId.ShouldBe(project.Id);
        item.Content.ShouldBe("hyphens intact");
        (await FromDb(db => db.Projects.CountAsync(
            p => p.RootPaths.Any(r => r.Contains("fh6")), Token))).ShouldBe(1);
    }

    [Fact]
    public async Task AnUnknownSlug_CreatesAPathIdentityProjectForItsDemangledRoot()
    {
        var demangled = MemorySlug.Demangle(Slug);
        WriteMemoryFile(Slug, "MEMORY.md", "fresh");

        await Scanner().ScanAsync(Token);

        var project = await FromDb(db => db.Projects.SingleAsync(p => p.Identity == demangled, Token));
        project.RootPaths.ShouldBe([demangled]);
        var item = await FromDb(db => db.HarvestedItems.SingleAsync(Token));
        item.ProjectId.ShouldBe(project.Id);
        item.Path.ShouldBe($"{Slug}/memory/MEMORY.md");
    }

    [Fact]
    public async Task AnUnchangedFile_GetsNoNewVersion()
    {
        WriteMemoryFile(Slug, "MEMORY.md", "stable");
        await Scanner().ScanAsync(Token);
        Clock.Advance(TimeSpan.FromMinutes(5));

        var second = await Scanner().ScanAsync(Token);

        second.Items.ShouldBe(1);
        second.Changed.ShouldBe(0);
        (await VersionsAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task AnEditedFile_GetsANewVersionAndThePriorIsKept()
    {
        WriteMemoryFile(Slug, "MEMORY.md", "first insight");
        await Scanner().ScanAsync(Token);
        Clock.Advance(TimeSpan.FromMinutes(5));
        WriteMemoryFile(Slug, "MEMORY.md", "sharper insight");

        var result = await Scanner().ScanAsync(Token);

        result.Changed.ShouldBe(1);
        var versions = await VersionsAsync();
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
        WriteMemoryFile(Slug, "MEMORY.md", "ephemeral");
        await Scanner().ScanAsync(Token);
        Clock.Advance(TimeSpan.FromMinutes(5));
        File.Delete(Path.Combine(_root, Slug, "memory", "MEMORY.md"));

        var result = await Scanner().ScanAsync(Token);

        result.Items.ShouldBe(0);
        result.Gone.ShouldBe(1);
        var version = (await VersionsAsync()).ShouldHaveSingleItem();
        version.GoneAt.ShouldBe(Now.AddMinutes(5));
        version.Content.ShouldBe("ephemeral");
    }

    [Fact]
    public async Task AReappearedFile_IsAliveAgainAndTheGoneVersionStaysInHistory()
    {
        WriteMemoryFile(Slug, "MEMORY.md", "resilient");
        await Scanner().ScanAsync(Token);
        Clock.Advance(TimeSpan.FromMinutes(5));
        File.Delete(Path.Combine(_root, Slug, "memory", "MEMORY.md"));
        await Scanner().ScanAsync(Token);
        Clock.Advance(TimeSpan.FromMinutes(5));
        WriteMemoryFile(Slug, "MEMORY.md", "resilient");

        await Scanner().ScanAsync(Token);

        var versions = await VersionsAsync();
        versions.Count.ShouldBe(2);
        versions[0].GoneAt.ShouldBe(Now.AddMinutes(5));
        versions[1].GoneAt.ShouldBeNull();
        versions[1].FirstSeen.ShouldBe(Now);
    }

    [Fact]
    public async Task OnlyMarkdownUnderAMemoryDirectoryIsHarvested()
    {
        WriteMemoryFile(Slug, "MEMORY.md", "yes");
        WriteMemoryFile(Slug, Path.Combine("deep", "nested.md"), "yes, recursively");
        WriteFile(Path.Combine(_root, Slug, "memory", "notes.txt"), "not markdown");
        WriteFile(Path.Combine(_root, Slug, "session.jsonl"), "not memory");
        WriteFile(Path.Combine(_root, "stray.md"), "not in a project");

        var result = await Scanner().ScanAsync(Token);

        result.Items.ShouldBe(2);
        var versions = await VersionsAsync();
        versions.Select(v => v.Path).ShouldBe(
            [$"{Slug}/memory/MEMORY.md", $"{Slug}/memory/deep/nested.md"], ignoreOrder: true);
    }

    [Fact]
    public async Task AnUnreadableFile_KeepsItsStateInsteadOfGoingGone()
    {
        WriteMemoryFile(Slug, "MEMORY.md", "locked later");
        await Scanner().ScanAsync(Token);
        Clock.Advance(TimeSpan.FromMinutes(5));
        var file = Path.Combine(_root, Slug, "memory", "MEMORY.md");

        HarvestScanResult result;
        using (MakeUnreadable(file))
        {
            result = await Scanner().ScanAsync(Token);
        }

        result.Items.ShouldBe(1, "an unreadable file was still seen");
        result.Gone.ShouldBe(0);
        var version = (await VersionsAsync()).ShouldHaveSingleItem();
        version.GoneAt.ShouldBeNull();
    }

    private static IDisposable MakeUnreadable(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            return File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None);
        }

        var mode = File.GetUnixFileMode(file);
        File.SetUnixFileMode(file, UnixFileMode.None);
        return new RestoreMode(file, mode);
    }

    private sealed record RestoreMode(string File, UnixFileMode Mode) : IDisposable
    {
        public void Dispose()
        {
            // Only ever constructed off-Windows; the guard is for the platform analyzer.
            if (!OperatingSystem.IsWindows())
            {
                System.IO.File.SetUnixFileMode(File, Mode);
            }
        }
    }

    [Fact]
    public async Task AMissingHarvestRoot_ThrowsInsteadOfMarkingEverythingGone()
    {
        WriteMemoryFile(Slug, "MEMORY.md", "still here");
        await Scanner().ScanAsync(Token);
        Directory.Delete(_root, recursive: true);

        await Should.ThrowAsync<DirectoryNotFoundException>(() => Scanner().ScanAsync(Token));

        Directory.CreateDirectory(_root); // so DisposeAsync still has something to delete
        (await VersionsAsync()).ShouldAllBe(v => v.GoneAt == null);
    }

    private HarvestScanner Scanner()
        => new(
            Context,
            new ProjectResolver(Context),
            Options.Create(new HarvestOptions { Root = _root }),
            Clock,
            NullLogger<HarvestScanner>.Instance);

    private void WriteMemoryFile(string slug, string relativePath, string content)
        => WriteFile(Path.Combine(_root, slug, "memory", relativePath), content);

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task<List<HarvestedItem>> VersionsAsync()
        => await FromDb(db => db.HarvestedItems
            .OrderBy(i => i.LastChanged).ThenBy(i => i.Id)
            .ToListAsync(Token));
}
