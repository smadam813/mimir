using Mimir.Server.Distillation;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Distillation;

internal sealed class FakeDistiller : IEpisodeDistiller
{
    private readonly Queue<IReadOnlyList<WisdomCandidate>> _answers = new();

    public Exception? Failure { get; set; }

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

        return _answers.Count > 0
            ? Task.FromResult(_answers.Dequeue())
            : throw new InvalidOperationException("no scripted answer left");
    }
}
