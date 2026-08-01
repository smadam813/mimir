using System.Reflection;

namespace Mimir.Server.Tests.Evaluation;

public sealed class GoldenSuiteContractTests
{
    [Fact]
    public void TheGoldenSuite_DoesNotInheritTheEmptiedThrowawayHarness()
        => typeof(GoldenSuiteTests).IsSubclassOf(typeof(PostgresTestBase)).ShouldBeFalse(
            "the §9 suite replays the GoldenCases in the development database, so over an emptied "
            + "throwaway it would sweep zero cases and pass forever");

    [Fact]
    public void TheGoldenSuite_CarriesTheOllamaTrait_SoCisZeroSkipRunNeverReachesIt()
        => typeof(GoldenSuiteTests)
            .GetCustomAttributes<TraitAttribute>()
            .ShouldContain(
                trait => trait.Name == "requires" && trait.Value == "ollama",
                "CI runs --filter \"requires!=ollama\" under FailSkips and never has Ollama, so an "
                + "untraited suite fails every build for a dependency CI is not meant to have");
}
