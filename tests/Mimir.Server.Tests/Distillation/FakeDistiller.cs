using Mimir.Server.Distillation;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// A scripted <see cref="IEpisodeDistiller"/>: enqueued answers are consumed in order, and with
/// nothing enqueued an Episode distils to no candidates — the empty answer §6 calls a fine one,
/// which keeps a queue-turn test to the turn it asserts. Set <see cref="Failure"/> for the
/// unusable-answer path. What the model said, and how many chunks it took to say it, is
/// <see cref="EpisodeDistillerTests"/>' subject and stops at this seam.
/// </summary>
internal sealed class FakeDistiller : IEpisodeDistiller
{
    private readonly Queue<IReadOnlyList<WisdomCandidate>> _answers = new();

    public Exception? Failure { get; set; }

    /// <summary>What each call was handed — the Episode, its Project's identity, its Events.</summary>
    public List<(Guid EpisodeId, string ProjectIdentity, IReadOnlyList<Event> Events)> Calls { get; } = [];

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

        return Task.FromResult<IReadOnlyList<WisdomCandidate>>(
            _answers.Count > 0 ? _answers.Dequeue() : []);
    }
}
