using Mimir.Server.Distillation;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// A scripted <see cref="IMergeArbiter"/>: enqueued rulings are consumed in order, and with
/// nothing enqueued a call rules Agreement on the existing text — the merge that changes no
/// wording, which keeps match-path tests focused on the mechanics they assert.
/// </summary>
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
