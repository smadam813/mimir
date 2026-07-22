using Mimir.Server.Configuration;
using Mimir.Server.Recall;

namespace Mimir.Server.Tests.Recall;

/// <summary>
/// The §7 brief_score, factor by factor: recency × salience × (1 + log₂(1 + reinforcement)).
/// Every constant asserted here is quoted from the spec's §11 knob table.
/// </summary>
public class RecallScoringTests
{
    private static readonly RecallOptions Options = new();

    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Recency_IsOneWhenJustConfirmed()
        => RecallScoring.Recency(Now, Now, Options).ShouldBe(1.0);

    [Fact]
    public void Recency_HalvesAtTheHalfLife()
        => RecallScoring.Recency(Now.AddDays(-90), Now, Options).ShouldBe(0.5, tolerance: 1e-9);

    [Fact]
    public void Recency_NeverDecaysBelowTheFloor()
        // 500 days is 5.6 half-lives — raw decay ≈ 0.02, but the §7 floor holds at 0.3.
        => RecallScoring.Recency(Now.AddDays(-500), Now, Options).ShouldBe(0.3);

    [Fact]
    public void BriefScore_GrowsWithLog2OfReinforcement()
    {
        // A just-confirmed, non-salient Wisdom isolates the reinforcement term.
        RecallScoring.BriefScore(reinforcement: 1, salient: false, Now, Now, Options)
            .ShouldBe(2.0, tolerance: 1e-9); // 1 + log₂(2)
        RecallScoring.BriefScore(reinforcement: 3, salient: false, Now, Now, Options)
            .ShouldBe(3.0, tolerance: 1e-9); // 1 + log₂(4)
    }

    [Fact]
    public void BriefScore_BoostsSalientWisdom()
    {
        var plain = RecallScoring.BriefScore(reinforcement: 1, salient: false, Now, Now, Options);
        var salient = RecallScoring.BriefScore(reinforcement: 1, salient: true, Now, Now, Options);

        salient.ShouldBe(plain * 1.3, tolerance: 1e-9);
    }

    [Fact]
    public void BriefScore_MultipliesAllThreeFactors()
        // Half-life-old, salient, reinforcement 3: 0.5 × 1.3 × 3.
        => RecallScoring.BriefScore(reinforcement: 3, salient: true, Now.AddDays(-90), Now, Options)
            .ShouldBe(0.5 * 1.3 * 3.0, tolerance: 1e-9);
}
