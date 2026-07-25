using Mimir.Server.Distillation;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// A scripted <see cref="IEpisodeDistiller"/>: enqueued answers are consumed in order, one per
/// call. An exhausted script throws rather than answering nothing, the way
/// <see cref="FakeChatClient"/> does — the empty answer §6 calls a fine one is written
/// <see cref="Enqueue()"/>, so a test that means "distilled to no candidates" says so and an
/// unscripted call is a call the test did not expect. That is what holds the seam's central
/// claim — one call per Episode, not per Event or per chunk — inside reach of a mutation check.
/// Set <see cref="Failure"/> for the unusable-answer path. What the model said, and how many
/// chunks it took to say it, is <see cref="EpisodeDistillerTests"/>' subject and stops here.
/// </summary>
internal sealed class FakeDistiller : IEpisodeDistiller
{
    private readonly Queue<IReadOnlyList<WisdomCandidate>> _answers = new();

    public Exception? Failure { get; set; }

    /// <summary>What each call was handed — the Episode, its Project's identity, its Events.</summary>
    public List<(Guid EpisodeId, string ProjectIdentity, IReadOnlyList<Event> Events)> Calls { get; } = [];

    /// <summary>Scripts one call's answer; with no candidates, one Episode distilled to none.</summary>
    public void Enqueue(params WisdomCandidate[] candidates) => _answers.Enqueue(candidates);

    public Task<IReadOnlyList<WisdomCandidate>> DistillAsync(
        Episode episode,
        string projectIdentity,
        IReadOnlyList<Event> events,
        CancellationToken cancellationToken)
    {
        Calls.Add((episode.Id, projectIdentity, events));
        if (Failure is not null)
        {
            throw Failure;
        }

        return _answers.Count > 0
            ? Task.FromResult(_answers.Dequeue())
            : throw new InvalidOperationException("no scripted answer left");
    }
}
