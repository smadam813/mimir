using Mimir.Server.Configuration;
using Mimir.Server.Recall;

namespace Mimir.Server.Tests.Recall;

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
        => RecallScoring.Recency(Now.AddDays(-500), Now, Options).ShouldBe(0.3);

    [Fact]
    public void BriefScore_GrowsWithLog2OfReinforcement()
    {
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
        => RecallScoring.BriefScore(reinforcement: 3, salient: true, Now.AddDays(-90), Now, Options)
            .ShouldBe(0.5 * 1.3 * 3.0, tolerance: 1e-9);

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
        => QueryScore(daysOld: 500).ShouldBe(0.3, tolerance: 1e-9);

    [Fact]
    public void QueryScore_BoostsSalience()
        => QueryScore(salient: true).ShouldBe(1.3, tolerance: 1e-9);

    [Fact]
    public void QueryScore_DampsReinforcementLogarithmically()
    {
        QueryScore(reinforcement: 1).ShouldBe(1 + (Math.Log(2) / 10), tolerance: 1e-9);
        QueryScore(reinforcement: 100).ShouldBe(1 + (Math.Log(101) / 10), tolerance: 1e-9);
    }

    [Fact]
    public void QueryScore_ScalesTheFusedRankByAllFactors()
        => QueryScore(fused: 2.0 / 61, projectAffinity: true, reinforcement: 3, salient: true, daysOld: 90)
            .ShouldBe(2.0 / 61 * 1.5 * 0.5 * 1.3 * (1 + (Math.Log(4) / 10)), tolerance: 1e-9);
}
