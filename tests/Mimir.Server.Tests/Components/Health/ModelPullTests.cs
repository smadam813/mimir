using Mimir.Contracts.Health;
using Mimir.Server.Components.Health;

namespace Mimir.Server.Tests.Components.Health;

public class ModelPullTests
{
    [Fact]
    public void NoModelsAtAll_IsNoPull()
        => ModelPull.From(Ollama()).ShouldBeNull();

    [Fact]
    public void ModelsThatAreNotPulling_AreNoPull()
        => ModelPull.From(Ollama(
                Model("qwen3:8b", ModelProvisioningState.Ready),
                Model("qwen3-embedding", ModelProvisioningState.Pending),
                Model("broken", ModelProvisioningState.Failed)))
            .ShouldBeNull();

    [Fact]
    public void ThePullingModel_IsNamedWithItsPercentage_PastModelsThatAreDone()
    {
        var pull = ModelPull.From(Ollama(
            Model("qwen3-embedding", ModelProvisioningState.Ready, percentComplete: 100),
            Model("qwen3:8b", ModelProvisioningState.Pulling, percentComplete: 42)));

        pull.ShouldNotBeNull();
        pull.Value.Name.ShouldBe("qwen3:8b");
        pull.Value.PercentComplete.ShouldBe(42);
    }

    [Fact]
    public void TwoModelsPullingAtOnce_NamesTheFirstTheTileLists()
    {
        var pull = ModelPull.From(Ollama(
            Model("qwen3:8b", ModelProvisioningState.Pulling, percentComplete: 12),
            Model("qwen3-embedding", ModelProvisioningState.Pulling, percentComplete: 90)));

        pull.ShouldNotBeNull();
        pull.Value.Name.ShouldBe("qwen3:8b");
        pull.Value.PercentComplete.ShouldBe(12);
    }

    [Fact]
    public void APullWithNoTotalReported_IsNamedWithoutAPercentage()
    {
        var pull = ModelPull.From(Ollama(Model("qwen3:8b", ModelProvisioningState.Pulling)));

        pull.ShouldNotBeNull();
        pull.Value.Name.ShouldBe("qwen3:8b");
        pull.Value.PercentComplete.ShouldBeNull();
    }

    private static OllamaTile Ollama(params ModelStatus[] models) => new()
    {
        State = HealthTileState.Working,
        Summary = "provisioning",
        Models = models,
    };

    private static ModelStatus Model(string name, ModelProvisioningState state, int? percentComplete = null)
        => new() { Name = name, State = state, PercentComplete = percentComplete };
}
