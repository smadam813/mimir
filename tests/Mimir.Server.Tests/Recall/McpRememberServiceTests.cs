using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Contracts.Mcp;
using Mimir.Server.Capture;
using Mimir.Server.Configuration;
using Mimir.Server.Recall;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Recall;

/// <summary>
/// <c>mimir_remember</c> (§4, §7.1) against a real Postgres: the save lands salient on the most
/// recently active unsealed Episode of the Project — activity, not start order — and with no
/// unsealed Episode the content goes straight through the Merge Gate as a candidate. A deliberate
/// save is never dropped.
/// </summary>
public sealed class McpRememberServiceTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task LandsSalient_OnTheMostRecentlyActiveUnsealedEpisode()
    {
        var project = await AddProjectAsync("mcp-remember");
        // Started earlier but active later — activity, not start order, picks the target (§7.1).
        var activeLater = await AddEpisodeAsync(project.Id, startedAt: Now.AddHours(-3));
        await AddEventAsync(activeLater.Id, seq: 1, at: Now.AddMinutes(-5));
        var startedLater = await AddEpisodeAsync(project.Id, startedAt: Now.AddHours(-1));
        await AddEpisodeAsync(project.Id, startedAt: Now.AddMinutes(-1), sealedAt: Now);

        var text = await RememberAsync(project, "Always run the linter before pushing.", "Lesson");

        text.ShouldContain(activeLater.SessionId);
        text.ShouldContain("salient");
        var saved = await FromDb(db => db.Events
            .SingleAsync(e => e.EpisodeId == activeLater.Id && e.Type == EventType.Remember, Token));
        saved.Salient.ShouldBeTrue();
        saved.Seq.ShouldBe(2);
        saved.Payload.ShouldContain("Always run the linter before pushing.");
        saved.Payload.ShouldContain("Lesson");
        (await FromDb(db => db.Events.CountAsync(e => e.EpisodeId == startedLater.Id, Token)))
            .ShouldBe(0, "the younger-but-idle Episode is not the most recently active");
    }

    [Fact]
    public async Task WithNoUnsealedEpisode_TheContentGoesThroughTheMergeGate()
    {
        var project = await AddProjectAsync("mcp-remember");
        await AddEpisodeAsync(project.Id, startedAt: Now.AddHours(-2), sealedAt: Now.AddHours(-1));
        const string content = "Prefer trunk-based development on this repo.";

        var text = await RememberAsync(project, content, "preference");

        text.ShouldContain("Merge Gate");
        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(Token));
        wisdom.Text.ShouldBe(content);
        wisdom.Kind.ShouldBe(WisdomKind.Preference);
        wisdom.ScopeProjectId.ShouldBe(project.Id);
        wisdom.Reinforcement.ShouldBe(1);
        (await FromDb(db => db.Provenance.CountAsync(Token)))
            .ShouldBe(0, "an Episode-less save has no provenance to point at — never an all-null row");
        (await FromDb(db => db.Events.CountAsync(Token)))
            .ShouldBe(0, "no Remember Event lands when no Episode is live");
    }

    [Fact]
    public async Task ACallerGivingUpMidAdmission_StillLandsTheSave()
    {
        var project = await AddProjectAsync("mcp-remember");
        await AddEpisodeAsync(project.Id, startedAt: Now.AddHours(-2), sealedAt: Now.AddHours(-1));
        const string content = "The gate outlasted the caller";

        // The gate's lock can be held across a background batch's arbiter calls, well past the
        // CLI's 30 s MCP timeout, and the endpoint's token is RequestAborted — so the caller can
        // vanish mid-admission. Standing in for that here: the token trips the moment the gate
        // starts work. Bound to it, the admission would roll back with nothing left to retry
        // from — no marker, no queue — and the save would be gone.
        using var abandoned = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Embeddings.OnGenerate = _ => abandoned.Cancel();

        await RememberAsync(project, content, "Fact", abandoned.Token);

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(Token));
        wisdom.Text.ShouldBe(content);
        wisdom.ScopeProjectId.ShouldBe(project.Id, "a deliberate save is never dropped (§7.1)");
    }

    [Fact]
    public async Task LongContent_IsStoredVerbatim_NeverTruncated()
    {
        var project = await AddProjectAsync("mcp-remember");
        var episode = await AddEpisodeAsync(project.Id, startedAt: Now.AddHours(-1));
        // Well past the §4 per-field cap (4 KB) — hook payloads would be clipped at this size.
        var content = new string('c', 10_000);

        await RememberAsync(project, content, "Fact");

        var saved = await FromDb(db => db.Events
            .SingleAsync(e => e.EpisodeId == episode.Id && e.Type == EventType.Remember, Token));
        saved.Payload.ShouldContain(content, customMessage:
            "spec §7.1: a deliberate save is never dropped — nor clipped");
        saved.Payload.ShouldNotContain("…[truncated");
    }

    [Fact]
    public async Task AnUnknownDirectory_StillLands_ByCreatingItsProject()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var identity = $"github.com/test/unseen-{suffix}";
        const string content = "Ship small, ship often.";

        var text = await Service().RememberAsync(
            new McpRememberRequest
            {
                ProjectIdentity = identity,
                ProjectRoot = $@"C:\roots\unseen-{suffix}",
                Content = content,
                Kind = "Fact",
            },
            Token);

        text.ShouldContain("Merge Gate");
        var project = await FromDb(db => db.Projects.SingleAsync(p => p.Identity == identity, Token));
        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(Token));
        wisdom.Text.ShouldBe(content);
        wisdom.ScopeProjectId.ShouldBe(project.Id, "a deliberate save is never dropped (§7.1)");
    }

    [Fact]
    public async Task AnUnknownKind_NamesTheVocabulary_AndWritesNothing()
    {
        var project = await AddProjectAsync("mcp-remember");
        await AddEpisodeAsync(project.Id, startedAt: Now.AddHours(-1));

        var text = await RememberAsync(project, "anything", "hunch");

        text.ShouldContain("Unknown kind 'hunch'");
        (await FromDb(db => db.Events.CountAsync(Token))).ShouldBe(0);
        (await FromDb(db => db.Wisdom.CountAsync(Token))).ShouldBe(0);
    }

    private async Task<string> RememberAsync(
        Project project, string content, string kind, CancellationToken? cancellationToken = null)
        => await Service().RememberAsync(
            new McpRememberRequest
            {
                ProjectIdentity = project.Identity,
                ProjectRoot = project.RootPaths[0],
                Content = content,
                Kind = kind,
            },
            cancellationToken ?? Token);

    private McpRememberService Service()
        => new(
            Context,
            new ProjectResolver(Context),
            new CaptureService(
                Context,
                new ProjectResolver(Context),
                Options.Create(new CaptureOptions()),
                Clock,
                new EpisodeFeed()),
            CreateMergeGate());
}
