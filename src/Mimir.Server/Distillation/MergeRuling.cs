using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Distillation;

internal abstract record MergeRuling
{
    private MergeRuling()
    {
    }

    public sealed record Agreement(string MergedText) : MergeRuling;

    public sealed record Supersede : MergeRuling;

    public sealed record ScopeSplit(string GlobalText, string ProjectText) : MergeRuling;
}

internal interface IMergeArbiter
{
    Task<MergeRuling> RuleAsync(Wisdom existing, WisdomCandidate candidate, CancellationToken cancellationToken);
}

internal sealed class MergeArbiterException(string message) : Exception(message);
