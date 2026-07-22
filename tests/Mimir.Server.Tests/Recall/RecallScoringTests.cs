using Mimir.Server.Configuration;
using Mimir.Server.Recall;

namespace Mimir.Server.Tests.Recall;

/// <summary>
/// The §7 scores, factor by factor: brief_score = recency × salience × (1 + log₂(1 +
/// reinforcement)), and the query ranking's per-hit multiplier over the fused rank — affinity ×
/// recency × salience × (1 + ln(1 + reinforcement)/10). Every constant asserted here is quoted
/// from the spec's §11 knob table.
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

    // A fresh, non-salient, reinforcement-0 hit isolates one factor at a time below. (Wisdom is
    // born at reinforcement 1, but 0 makes the damping term exactly 1 for isolation.)
    private static double QueryScore(
        double fused = 1.0,
        bool projectAffinity = false,
        int reinforcement = 0,
        bool salient = false,
        double daysOld = 0)
        => RecallScoring.QueryScore(
            fused, projectAffinity, reinforcement, salient, Now.AddDays(-daysOld), Now, Options);

    [Fact]
    public void QueryScore_BoostsProjectAffinity()
        => QueryScore(projectAffinity: true).ShouldBe(1.5, tolerance: 1e-9);

    [Fact]
    public void QueryScore_LeavesGlobalScopeUnboosted()
        => QueryScore(projectAffinity: false).ShouldBe(1.0, tolerance: 1e-9);

    [Fact]
    public void QueryScore_RecencyHoldsAtTheFloor()
        // 500 days is 5.6 half-lives — raw decay ≈ 0.02, but the §7 floor holds at 0.3.
        => QueryScore(daysOld: 500).ShouldBe(0.3, tolerance: 1e-9);

    [Fact]
    public void QueryScore_BoostsSalience()
        => QueryScore(salient: true).ShouldBe(1.3, tolerance: 1e-9);

    [Fact]
    public void QueryScore_DampsReinforcementLogarithmically()
    {
        // 1 + ln(1+n)/10: reinforcement grows the score, but even 100 confirmations add < 50%.
        QueryScore(reinforcement: 1).ShouldBe(1 + (Math.Log(2) / 10), tolerance: 1e-9);
        QueryScore(reinforcement: 100).ShouldBe(1 + (Math.Log(101) / 10), tolerance: 1e-9);
    }

    [Fact]
    public void QueryScore_ScalesTheFusedRankByAllFactors()
        // RRF rank-1-both-legs fused (2/61), project-scoped, half-life-old, salient, reinforced 3.
        => QueryScore(fused: 2.0 / 61, projectAffinity: true, reinforcement: 3, salient: true, daysOld: 90)
            .ShouldBe(2.0 / 61 * 1.5 * 0.5 * 1.3 * (1 + (Math.Log(4) / 10)), tolerance: 1e-9);
}
