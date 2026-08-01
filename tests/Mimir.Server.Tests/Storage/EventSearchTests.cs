using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Storage;

/// <summary>
/// The Episode leg of <c>mimir_search</c> against a real Postgres: FTS-only over
/// <c>Event.tsv</c>, with every filter and the cap in the one statement.
/// </summary>
public sealed class EventSearchTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task AHit_CarriesItsEpisode_SoATimelineEntryNeedsNoSecondQuery()
    {
        var project = await AddProjectAsync("event-search");
        var episode = await AddEpisodeAsync(
            project.Id, startedAt: Now.AddHours(-2), sealedAt: Now.AddHours(-1), sealReason: "clear");
        var evt = await AddEventAsync(
            episode.Id, seq: 4, EventType.PostToolUse, at: Now.AddMinutes(-90),
            payload: """{"tool_name":"quokka"}""");

        var hit = (await NewSearch().SearchAsync("quokka", null, null, 10, Token)).ShouldHaveSingleItem();

        hit.EventId.ShouldBe(evt.Id);
        hit.EpisodeId.ShouldBe(episode.Id);
        hit.Seq.ShouldBe(4);
        hit.Type.ShouldBe(nameof(EventType.PostToolUse), "Type arrives as the stored enum string");
        hit.At.ShouldBe(Now.AddMinutes(-90));
        hit.SessionId.ShouldBe(episode.SessionId);
        hit.ProjectId.ShouldBe(project.Id);
        hit.StartedAt.ShouldBe(episode.StartedAt);
        hit.SealedAt.ShouldBe(episode.SealedAt);
        hit.SealReason.ShouldBe("clear");
    }

    [Fact]
    public async Task ALargePayload_IsClippedServerSide_ToAPreview()
    {
        var project = await AddProjectAsync("event-search");
        var episode = await AddEpisodeAsync(project.Id);
        var padding = new string('y', 20_000);
        await AddEventAsync(
            episode.Id, seq: 1, payload: $$"""{"prompt":"quokka","padding":"{{padding}}"}""");

        var hit = (await NewSearch().SearchAsync("quokka", null, null, 10, Token)).ShouldHaveSingleItem();

        hit.Payload.Length.ShouldBe(
            1000, "stored payloads run to tens of KB, so the clip happens in SQL, not after transfer");
    }

    [Fact]
    public async Task TheCallersCap_IsTheQueryLimit()
    {
        var project = await AddProjectAsync("event-search");
        var episode = await AddEpisodeAsync(project.Id);
        for (var seq = 1; seq <= 5; seq++)
        {
            await AddEventAsync(episode.Id, seq, payload: """{"prompt":"quokka"}""");
        }

        var hits = await NewSearch().SearchAsync("quokka", null, null, 2, Token);

        hits.Count.ShouldBe(2, "rank and filters are wholly in SQL, so nothing is over-fetched and trimmed");
    }

    [Fact]
    public async Task Since_KeepsOnlyEventsCapturedAtOrAfterTheInstant()
    {
        var project = await AddProjectAsync("event-search");
        var episode = await AddEpisodeAsync(project.Id, startedAt: Now.AddHours(-5));
        await AddEventAsync(episode.Id, seq: 1, at: Now.AddHours(-4), payload: """{"prompt":"quokka"}""");
        var onTheInstant = await AddEventAsync(
            episode.Id, seq: 2, at: Now.AddHours(-2), payload: """{"prompt":"quokka"}""");
        var after = await AddEventAsync(
            episode.Id, seq: 3, at: Now.AddHours(-1), payload: """{"prompt":"quokka"}""");

        var hits = await NewSearch().SearchAsync("quokka", null, Now.AddHours(-2), 10, Token);

        hits.Select(h => h.EventId).ShouldBe([onTheInstant.Id, after.Id], ignoreOrder: true);
    }

    private EventSearch NewSearch() => new(Context);
}
