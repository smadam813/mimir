using Mimir.Contracts.Health;
using Mimir.Server.Components.Health;

namespace Mimir.Server.Tests.Components.Health;

/// <summary>
/// The header's first-run pull readout (#90), read off a health snapshot alone — no database, so
/// these run on a machine with no Postgres, which is where a first run actually happens.
/// </summary>
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
        // "At most one pulls at a time" is ModelProvisioner's sequential loop, not something the
        // tile enforces — so what the header does if that ever stops holding is a decision, not an
        // accident: the first listed, in the order §11 declares the models. The Health popover
        // beside the chip is what states every model's own state.
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
