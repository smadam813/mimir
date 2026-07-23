using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Capture;
using Mimir.Server.Tests.Distillation;
using Mimir.Server.Ui;
using Pgvector;

namespace Mimir.Server.Tests.Ui;

/// <summary>
/// Spec §8.3 against a real Postgres: the injection log's per-session listing with sizes and
/// hydrated items, the one-click §9 marks with <c>verdict_at</c>, the injection-precision
/// inputs, and promote-to-golden — filled from the entry's <c>query_context</c> and
/// <c>project_id</c>, refused for Brief entries, idempotent on repeat clicks.
/// </summary>
public sealed class InjectionBrowserTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);

    private FixtureContextFactory Contexts => new(fixture);

    [Fact]
    public async Task TheListing_GroupsPerSessionNewestFirst_AndCarriesTheEntrysShape()
    {
        var project = await SeedProjectAsync();
        var wisdom = await SeedWisdomAsync(project.Id);
        var early = await SeedInjectionAsync(
            project.Id, "sess-a", InjectionLane.Brief, queryContext: null, Now.AddMinutes(-10),
            items: [(wisdom.Id, 0.02)]);
        var late = await SeedInjectionAsync(
            project.Id, "sess-b", InjectionLane.Prompt, "how do I deploy?", Now,
            items: [(wisdom.Id, 0.03)]);

        var view = await Browser().ListAsync(project.Id, Token);

        view.Sessions.Select(s => s.SessionId).ShouldBe(["sess-b", "sess-a"]);
        var entry = view.Sessions[0].Entries.ShouldHaveSingleItem();
        entry.Id.ShouldBe(late.Id);
        entry.Lane.ShouldBe(InjectionLane.Prompt);
        entry.QueryContext.ShouldBe("how do I deploy?");
        entry.Chars.ShouldBe(late.Chars);
        entry.CanPromote.ShouldBeTrue();
        view.Sessions[1].Entries.ShouldHaveSingleItem().Id.ShouldBe(early.Id);
        view.Sessions[1].Entries[0].CanPromote.ShouldBeFalse();
    }

    [Fact]
    public async Task TheListing_IsScopedToTheProject()
    {
        var (project, other) = (await SeedProjectAsync(), await SeedProjectAsync());
        var wisdom = await SeedWisdomAsync(project.Id);
        await SeedInjectionAsync(
            other.Id, "sess-other", InjectionLane.Prompt, "elsewhere", Now,
            items: [(wisdom.Id, 0.03)]);

        var view = await Browser().ListAsync(project.Id, Token);

        view.Sessions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Items_ArriveInStoredOrder_AsTheSameCardEntriesTheBrowserRenders()
    {
        var project = await SeedProjectAsync();
        var first = await SeedWisdomAsync(project.Id, "the stronger match");
        var second = await SeedWisdomAsync(project.Id, "the weaker match");
        await SeedInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "a prompt", Now,
            items: [(first.Id, 0.03), (second.Id, 0.02)]);

        var entry = (await Browser().ListAsync(project.Id, Token))
            .Sessions.ShouldHaveSingleItem().Entries.ShouldHaveSingleItem();

        entry.Items.Select(i => i.WisdomId).ShouldBe([first.Id, second.Id]);
        entry.Items[0].Score.ShouldBe(0.03);
        var card = entry.Items[0].Wisdom.ShouldNotBeNull();
        card.Text.ShouldBe("the stronger match");
        card.ScopeProjectId.ShouldBe(project.Id);
    }

    [Fact]
    public async Task AHardDeletedWisdom_LeavesItsItemVisible_WithoutACard()
    {
        var project = await SeedProjectAsync();
        var wisdom = await SeedWisdomAsync(project.Id);
        await SeedInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "a prompt", Now,
            items: [(wisdom.Id, 0.03)]);
        await IntoDb(db => db.Wisdom.Where(w => w.Id == wisdom.Id).ExecuteDeleteAsync(Token));

        var entry = (await Browser().ListAsync(project.Id, Token))
            .Sessions.ShouldHaveSingleItem().Entries.ShouldHaveSingleItem();

        var item = entry.Items.ShouldHaveSingleItem();
        item.WisdomId.ShouldBe(wisdom.Id);
        item.Wisdom.ShouldBeNull();
    }

    [Fact]
    public async Task Marking_SticksWithVerdictAt_AndRemarkingSwitches()
    {
        var project = await SeedProjectAsync();
        var wisdom = await SeedWisdomAsync(project.Id);
        var injection = await SeedInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "a prompt", Now,
            items: [(wisdom.Id, 0.03)]);

        await Browser().MarkAsync(injection.Id, InjectionVerdict.Useful, Token);

        var marked = (await Browser().ListAsync(project.Id, Token))
            .Sessions.Single().Entries.Single();
        marked.Verdict.ShouldBe(InjectionVerdict.Useful);
        marked.VerdictAt.ShouldBe(Now);

        _clock.Advance(TimeSpan.FromMinutes(5));
        await Browser().MarkAsync(injection.Id, InjectionVerdict.Noise, Token);

        var remarked = (await Browser().ListAsync(project.Id, Token))
            .Sessions.Single().Entries.Single();
        remarked.Verdict.ShouldBe(InjectionVerdict.Noise);
        remarked.VerdictAt.ShouldBe(Now.AddMinutes(5));
    }

    [Fact]
    public async Task Precision_IsUsefulOverMarked_UnmarkedEntriesStayOut()
    {
        var project = await SeedProjectAsync();
        var wisdom = await SeedWisdomAsync(project.Id);
        var items = new[] { (wisdom.Id, 0.03) };
        var useful1 = await SeedInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "p1", Now, items);
        var useful2 = await SeedInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "p2", Now, items);
        var noise = await SeedInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "p3", Now, items);
        await SeedInjectionAsync(project.Id, "sess-a", InjectionLane.Brief, null, Now, items);

        await Browser().MarkAsync(useful1.Id, InjectionVerdict.Useful, Token);
        await Browser().MarkAsync(useful2.Id, InjectionVerdict.Useful, Token);
        await Browser().MarkAsync(noise.Id, InjectionVerdict.Noise, Token);

        var view = await Browser().ListAsync(project.Id, Token);

        view.Useful.ShouldBe(2);
        view.Marked.ShouldBe(3);
        view.Precision.ShouldNotBeNull().ShouldBe(2.0 / 3.0, tolerance: 1e-9);
    }

    [Fact]
    public async Task PrecisionIsNull_UntilAnythingIsMarked()
    {
        var project = await SeedProjectAsync();

        var view = await Browser().ListAsync(project.Id, Token);

        view.Marked.ShouldBe(0);
        view.Precision.ShouldBeNull();
    }

    [Fact]
    public async Task Promoting_FillsTheCaseFromTheEntry_ExpectingItsTopRankedWisdom()
    {
        var project = await SeedProjectAsync();
        var top = await SeedWisdomAsync(project.Id, "the top match");
        var runnerUp = await SeedWisdomAsync(project.Id, "the runner-up");
        var injection = await SeedInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "how do I deploy?", Now,
            items: [(top.Id, 0.03), (runnerUp.Id, 0.02)]);

        var caseId = await Browser().PromoteAsync(injection.Id, Token);

        var goldenCase = await FromDb(db =>
            db.GoldenCases.SingleAsync(g => g.CreatedFromInjectionId == injection.Id, Token));
        goldenCase.Id.ShouldBe(caseId.ShouldNotBeNull());
        goldenCase.QueryContext.ShouldBe("how do I deploy?");
        goldenCase.ProjectId.ShouldBe(project.Id);
        goldenCase.ExpectedWisdomId.ShouldBe(top.Id);
        goldenCase.CreatedFromInjectionId.ShouldBe(injection.Id);
        goldenCase.Note.ShouldNotBeEmpty();

        var entry = (await Browser().ListAsync(project.Id, Token))
            .Sessions.Single().Entries.Single();
        entry.PromotedCaseId.ShouldBe(caseId);
    }

    [Fact]
    public async Task Promoting_FallsToTheNextSurvivingItem_WhenTheTopWisdomWasDeleted()
    {
        var project = await SeedProjectAsync();
        var top = await SeedWisdomAsync(project.Id);
        var runnerUp = await SeedWisdomAsync(project.Id);
        var injection = await SeedInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "a prompt", Now,
            items: [(top.Id, 0.03), (runnerUp.Id, 0.02)]);
        await IntoDb(db => db.Wisdom.Where(w => w.Id == top.Id).ExecuteDeleteAsync(Token));

        var caseId = await Browser().PromoteAsync(injection.Id, Token);

        var goldenCase = await FromDb(db =>
            db.GoldenCases.SingleAsync(g => g.CreatedFromInjectionId == injection.Id, Token));
        goldenCase.Id.ShouldBe(caseId.ShouldNotBeNull());
        goldenCase.ExpectedWisdomId.ShouldBe(runnerUp.Id);
    }

    [Fact]
    public async Task Promoting_SkipsARetiredWisdom_RecallWouldNeverSurfaceIt()
    {
        var project = await SeedProjectAsync();
        var top = await SeedWisdomAsync(project.Id);
        var runnerUp = await SeedWisdomAsync(project.Id);
        var injection = await SeedInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "a prompt", Now,
            items: [(top.Id, 0.03), (runnerUp.Id, 0.02)]);
        await RetireAsync(top.Id);

        var caseId = await Browser().PromoteAsync(injection.Id, Token);

        var goldenCase = await FromDb(db =>
            db.GoldenCases.SingleAsync(g => g.CreatedFromInjectionId == injection.Id, Token));
        goldenCase.Id.ShouldBe(caseId.ShouldNotBeNull());
        goldenCase.ExpectedWisdomId.ShouldBe(runnerUp.Id);
    }

    [Fact]
    public async Task AnEntryWithNoLiveWisdomLeft_CannotPromote()
    {
        var project = await SeedProjectAsync();
        var retired = await SeedWisdomAsync(project.Id);
        var deleted = await SeedWisdomAsync(project.Id);
        var injection = await SeedInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "a prompt", Now,
            items: [(retired.Id, 0.03), (deleted.Id, 0.02)]);
        await RetireAsync(retired.Id);
        await IntoDb(db => db.Wisdom.Where(w => w.Id == deleted.Id).ExecuteDeleteAsync(Token));

        var entry = (await Browser().ListAsync(project.Id, Token))
            .Sessions.ShouldHaveSingleItem().Entries.ShouldHaveSingleItem();
        entry.CanPromote.ShouldBeFalse();

        var caseId = await Browser().PromoteAsync(injection.Id, Token);

        caseId.ShouldBeNull();
        (await FromDb(db =>
                db.GoldenCases.AnyAsync(g => g.CreatedFromInjectionId == injection.Id, Token)))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task TheListing_BoundsToTheMostRecentEntries_PrecisionCountsThemAll()
    {
        var project = await SeedProjectAsync();
        var wisdom = await SeedWisdomAsync(project.Id);
        var oldest = await SeedInjectionAsync(
            project.Id, "sess-old", InjectionLane.Prompt, "the cut entry", Now.AddMinutes(-1),
            items: [(wisdom.Id, 0.03)]);
        await Browser().MarkAsync(oldest.Id, InjectionVerdict.Useful, Token);
        await IntoDb(db =>
        {
            for (var i = 0; i < InjectionBrowser.RecentEntryLimit; i++)
            {
                db.Injections.Add(new Injection
                {
                    Id = Guid.CreateVersion7(),
                    SessionId = "sess-a",
                    ProjectId = project.Id,
                    At = Now,
                    Lane = InjectionLane.Prompt,
                    QueryContext = $"prompt {i}",
                    Chars = 240,
                    Items = [new InjectionItem { WisdomId = wisdom.Id, Score = 0.03 }],
                });
            }

            return db.SaveChangesAsync(Token);
        });

        var view = await Browser().ListAsync(project.Id, Token);

        view.TotalEntries.ShouldBe(InjectionBrowser.RecentEntryLimit + 1);
        view.Truncated.ShouldBeTrue();
        view.Sessions.Sum(s => s.Entries.Count).ShouldBe(InjectionBrowser.RecentEntryLimit);
        view.Sessions.SelectMany(s => s.Entries).ShouldAllBe(e => e.Id != oldest.Id);
        // The cut entry's mark still feeds the §9 precision inputs.
        view.Useful.ShouldBe(1);
        view.Marked.ShouldBe(1);
    }

    [Fact]
    public async Task ABriefEntry_CannotPromote_ThereIsNoQueryToReplay()
    {
        var project = await SeedProjectAsync();
        var wisdom = await SeedWisdomAsync(project.Id);
        var injection = await SeedInjectionAsync(
            project.Id, "sess-a", InjectionLane.Brief, queryContext: null, Now,
            items: [(wisdom.Id, 0.03)]);

        var caseId = await Browser().PromoteAsync(injection.Id, Token);

        caseId.ShouldBeNull();
        (await FromDb(db =>
                db.GoldenCases.AnyAsync(g => g.CreatedFromInjectionId == injection.Id, Token)))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task Promoting_IsIdempotent_ARepeatClickReturnsTheExistingCase()
    {
        var project = await SeedProjectAsync();
        var wisdom = await SeedWisdomAsync(project.Id);
        var injection = await SeedInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "a prompt", Now,
            items: [(wisdom.Id, 0.03)]);

        var first = await Browser().PromoteAsync(injection.Id, Token);
        var second = await Browser().PromoteAsync(injection.Id, Token);

        second.ShouldBe(first.ShouldNotBeNull());
        (await FromDb(db =>
                db.GoldenCases.CountAsync(g => g.CreatedFromInjectionId == injection.Id, Token)))
            .ShouldBe(1);
    }

    private InjectionBrowser Browser() => new(Contexts, _clock);

    private async Task<Project> SeedProjectAsync()
    {
        var project = TestData.NewProject("injection");
        await IntoDb(db =>
        {
            db.Projects.Add(project);
            return db.SaveChangesAsync(Token);
        });
        return project;
    }

    private async Task<Wisdom> SeedWisdomAsync(Guid scopeProjectId, string text = "a wisdom")
    {
        var wisdom = new Wisdom
        {
            Id = Guid.CreateVersion7(),
            Kind = WisdomKind.Fact,
            ScopeProjectId = scopeProjectId,
            Text = text,
            Embedding = new Vector(TestVectors.WithCosine(0.5)),
            Reinforcement = 1,
            LastConfirmedAt = Now,
        };
        await IntoDb(db =>
        {
            db.Wisdom.Add(wisdom);
            return db.SaveChangesAsync(Token);
        });
        return wisdom;
    }

    private async Task<Injection> SeedInjectionAsync(
        Guid projectId,
        string sessionId,
        InjectionLane lane,
        string? queryContext,
        DateTimeOffset at,
        IReadOnlyList<(Guid WisdomId, double Score)> items)
    {
        var injection = new Injection
        {
            Id = Guid.CreateVersion7(),
            SessionId = sessionId,
            ProjectId = projectId,
            At = at,
            Lane = lane,
            QueryContext = queryContext,
            Chars = 240,
            Items = items.Select(i => new InjectionItem { WisdomId = i.WisdomId, Score = i.Score }).ToList(),
        };
        await IntoDb(db =>
        {
            db.Injections.Add(injection);
            return db.SaveChangesAsync(Token);
        });
        return injection;
    }

    private Task RetireAsync(Guid wisdomId)
        => IntoDb(db => db.Wisdom.Where(w => w.Id == wisdomId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(w => w.RetiredAt, (DateTimeOffset?)Now), Token));

    private async Task IntoDb(Func<MimirDbContext, Task> mutate)
    {
        await using var db = Contexts.CreateDbContext();
        await mutate(db);
    }

    private async Task<T> FromDb<T>(Func<MimirDbContext, Task<T>> query)
    {
        await using var db = Contexts.CreateDbContext();
        return await query(db);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
