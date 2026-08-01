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

        // That the clip is `left(...)` in SQL rather than a Substring after transfer is what it is
        // for, and is not what this pins: a client-side trim gives the same 1000 chars. Only the
        // length is pinned here; where the clip happens is doc-only in .claude/rules/storage.md.
        hit.Payload.Length.ShouldBe(1000, "a hit carries a preview, not the whole stored payload");
    }

    [Fact]
    public async Task TheCallersCap_IsTheQueryLimit()
    {
        var project = await AddProjectAsync("event-search");
        var episode = await AddEpisodeAsync(project.Id);
        for (var seq = 1; seq <= 5; seq++)
        {
            // Distinct text per row, so ts_rank_cd does not tie across the whole population and
            // leave the rank leg of the ORDER BY exercised only through its id tiebreak.
            await AddEventAsync(episode.Id, seq, payload: $$"""{"prompt":"quokka {{new string('q', seq)}}"}""");
        }

        var hits = await NewSearch().SearchAsync("quokka", null, null, 2, Token);

        // Like the clip above: a client-side Take(2) gives the same count, so "the LIMIT is in the
        // statement" is not what this pins — only that the caller's cap is honoured.
        hits.Count.ShouldBe(2, "the caller's cap bounds the result set");
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
