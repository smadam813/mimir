using Mimir.Server.Distillation;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// §6 chunking: chronological ~token windows, nothing lost, and Remember Events riding along in
/// every chunk. Budgets in these tests are tiny so a handful of Events spans several chunks.
/// </summary>
public class EpisodeChunkerTests
{
    [Fact]
    public void AnEpisodeWithinTheBudget_IsOneChunk_InSeqOrder()
    {
        var events = new[] { NewEvent(2, 40), NewEvent(1, 40), NewEvent(3, 40) };

        var chunks = EpisodeChunker.Chunk(events, chunkTokens: 1000);

        var chunk = chunks.ShouldHaveSingleItem();
        chunk.Select(e => e.Seq).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void NoEvents_MeansNoChunks()
        => EpisodeChunker.Chunk([], chunkTokens: 1000).ShouldBeEmpty();

    [Fact]
    public void AnOversizedEpisode_SplitsChronologically_LosingNothing()
    {
        // 100 tokens each (16 overhead + 336/4) against a 250-token budget: two per chunk.
        var events = Enumerable.Range(1, 6).Select(seq => NewEvent(seq, 336)).ToList();

        var chunks = EpisodeChunker.Chunk(events, chunkTokens: 250);

        chunks.Count.ShouldBe(3);
        chunks.SelectMany(c => c.Select(e => e.Seq)).ShouldBe([1, 2, 3, 4, 5, 6]);
    }

    [Fact]
    public void RememberEvents_RideAlongInEveryChunk()
    {
        var events = Enumerable.Range(1, 6).Select(seq => NewEvent(seq, 336)).ToList();
        events.Add(NewEvent(7, 40, EventType.Remember));

        var chunks = EpisodeChunker.Chunk(events, chunkTokens: 250);

        chunks.Count.ShouldBeGreaterThan(1);
        foreach (var chunk in chunks)
        {
            chunk.Count(e => e.Type == EventType.Remember).ShouldBe(1);
            chunk.Select(e => e.Seq).ShouldBeInOrder();
        }
    }

    [Fact]
    public void ASingleEventOverTheBudget_StillGetsAChunk()
    {
        var events = new[] { NewEvent(1, 40), NewEvent(2, 9000), NewEvent(3, 40) };

        var chunks = EpisodeChunker.Chunk(events, chunkTokens: 100);

        chunks.SelectMany(c => c.Select(e => e.Seq)).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void AnEpisodeOfOnlyRememberEvents_IsOneChunk_EvenOverBudget()
    {
        var events = Enumerable.Range(1, 5)
            .Select(seq => NewEvent(seq, 400, EventType.Remember))
            .ToList();

        var chunks = EpisodeChunker.Chunk(events, chunkTokens: 100);

        var chunk = chunks.ShouldHaveSingleItem();
        chunk.Select(e => e.Seq).ShouldBe([1, 2, 3, 4, 5]);
    }

    private static Event NewEvent(int seq, int payloadChars, EventType type = EventType.PostToolUse) => new()
    {
        Id = Guid.CreateVersion7(),
        EpisodeId = Guid.Empty,
        Seq = seq,
        Type = type,
        At = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero).AddMinutes(seq),
        Payload = new string('x', payloadChars),
        Salient = type == EventType.Remember,
    };
}
