using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Distillation;

/// <summary>
/// The §6 steps 3–4 ruling on a candidate that matched existing Wisdom (cosine ≥ 0.80): the pair
/// either agree — merged into one rewrite — or contradict, resolved by Supersede or Scope-split.
/// A closed hierarchy; the gate exhaustively switches over it.
/// </summary>
internal abstract record MergeRuling
{
    private MergeRuling()
    {
    }

    /// <summary>§6.3: the pair agree; <paramref name="MergedText"/> is the rewrite from both.</summary>
    public sealed record Agreement(string MergedText) : MergeRuling;

    /// <summary>
    /// §6.4: the candidate makes the existing Wisdom obsolete — it is inserted as new Wisdom and
    /// the old one is Retired with a <c>superseded_by</c> link.
    /// </summary>
    public sealed record Supersede : MergeRuling;

    /// <summary>
    /// §6.4: both hold, in different Scopes — rewritten into one Global and one Project-scoped
    /// Wisdom (<c>cause=adjudicated</c>).
    /// </summary>
    public sealed record ScopeSplit(string GlobalText, string ProjectText) : MergeRuling;
}

/// <summary>
/// The LLM half of the Merge Gate (§6 steps 3–4): classify a matched pair as agreement or
/// contradiction and produce the rewrite or adjudication. Backed by the distiller model
/// (qwen3:8b, <c>/no_think</c>) through the §2 model-client layer; faked in gate tests.
/// </summary>
internal interface IMergeArbiter
{
    /// <exception cref="MergeArbiterException">
    /// The model's answer was unusable. Callers let it propagate: the harvest marker (§5) keeps
    /// the item pending, so admission retries on a later tick instead of silently degrading a
    /// contradiction into a mechanical merge.
    /// </exception>
    Task<MergeRuling> RuleAsync(Wisdom existing, WisdomCandidate candidate, CancellationToken cancellationToken);
}

/// <summary>The arbiter's answer could not be turned into a <see cref="MergeRuling"/>.</summary>
internal sealed class MergeArbiterException(string message) : Exception(message);
