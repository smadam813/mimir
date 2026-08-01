using Mimir.Server.Distillation;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Distillation;

internal sealed class FakeArbiter : IMergeArbiter
{
    private readonly Queue<MergeRuling> _rulings = new();

    public Exception? Failure { get; set; }

    public List<(string ExistingText, string CandidateText)> Calls { get; } = [];

    public void Enqueue(MergeRuling ruling) => _rulings.Enqueue(ruling);

    public Task<MergeRuling> RuleAsync(
        Wisdom existing, WisdomCandidate candidate, CancellationToken cancellationToken)
    {
        Calls.Add((existing.Text, candidate.Text));
        if (Failure is not null)
        {
            throw Failure;
        }

        return Task.FromResult(_rulings.Count > 0
            ? _rulings.Dequeue()
            : new MergeRuling.Agreement(existing.Text));
    }
}
