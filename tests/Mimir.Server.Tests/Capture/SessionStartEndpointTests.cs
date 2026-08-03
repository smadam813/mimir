using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mimir.Contracts.Hooks;
using Mimir.Server.Capture;
using Mimir.Server.Configuration;
using Mimir.Server.Recall;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Capture;

/// <summary>
/// SessionStart, and the re-fire a context compaction sends after it. Compaction arrives on the
/// same session id as the start it follows, which is the whole reason the route needs no branch
/// for it — and equally the reason nothing here reads the <c>source</c> the payload carries.
/// </summary>
public sealed class SessionStartEndpointTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task ACompactRefire_ResumesTheOneEpisode_AndRecomposesAgainstCurrentWisdom()
    {
        var request = Request();
        var known = await AddWisdomAsync(Project.GlobalId, "wisdom the session started with");

        var start = await InvokeAsync(request);
        var episode = await FromDb(
            db => db.Episodes.SingleAsync(e => e.SessionId == request.SessionId, Token));
        var learnt = await AddWisdomAsync(Project.GlobalId, "wisdom distilled since the session started");
        var compact = await InvokeAsync(request with { Payload = Payload(source: "compact") });

        start.Brief.ShouldContain(known.Text);
        start.Brief.ShouldNotContain(learnt.Text);
        compact.Brief.ShouldContain(known.Text);
        compact.Brief.ShouldContain(
            learnt.Text,
            customMessage: "the re-fire re-reads the ambient universe rather than replaying the start");
        (await FromDb(db => db.Episodes.Where(e => e.SessionId == request.SessionId).ToListAsync(Token)))
            .ShouldHaveSingleItem().Id.ShouldBe(episode.Id, "a compaction resumes, it does not restart");
    }

    private async Task<SessionStartReply> InvokeAsync(HookEventRequest request)
    {
        var brief = new BriefService(
            Context,
            CreateWisdomSearch(),
            new InjectionLog(Context, Clock),
            Options.Create(new RecallOptions()),
            Clock,
            NullLogger<BriefService>.Instance);
        return await CaptureEndpoints.SessionStartAsync(request, CreateCaptureService(), brief, Token);
    }

    private static HookEventRequest Request()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new HookEventRequest
        {
            SessionId = $"sess-{suffix}",
            Cwd = $@"C:\git\session-start-{suffix}",
            ProjectIdentity = $"github.com/test/session-start-{suffix}",
            ProjectRoot = $@"C:\git\session-start-{suffix}",
            HookEvent = HookEvents.SessionStart,
            Payload = Payload(source: "startup"),
        };
    }

    private static JsonElement Payload(string source)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { source }));
        return document.RootElement.Clone();
    }
}
