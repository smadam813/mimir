using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Mimir.Contracts.Health;
using Mimir.Server.Components.Health;
using Mimir.Server.Health;

namespace Mimir.Server.Tests.Components.Health;

/// <summary>
/// The first-run header's pull chip. <see cref="ModelPullTests"/> pins which model
/// <c>ModelPull.From</c> picks and when it picks none; this pins what the chip does with each
/// answer — chiefly that "none" is no chip at all rather than an empty frame the header has to lay
/// out around.
/// <para>
/// Disconnected tier, like <see cref="HealthPillTests"/>: both read the same pushed snapshot.
/// </para>
/// </summary>
public class ModelPullChipTests : RenderTestBase
{
    private readonly HealthState _health = new();

    public ModelPullChipTests() => Services.AddSingleton<IHealthState>(_health);

    private void Publish(params ModelStatus[] models) => _health.Update(_ => HealthSnapshot.Pending with
    {
        Ollama = new OllamaTile
        {
            State = HealthTileState.Working,
            Summary = "Provisioning",
            Models = models,
        },
    });

    private static ModelStatus Model(string name, ModelProvisioningState state, int? percent = null)
        => new() { Name = name, State = state, PercentComplete = percent };

    /// <summary>
    /// Nothing in flight, nothing drawn. Every §11 state that is not a pull is here rather than
    /// just one, because the chip's <c>@if</c> is the header's only defence against a frame that
    /// says nothing sitting beside the health pill for the life of the install.
    /// </summary>
    [Theory]
    [InlineData(ModelProvisioningState.Ready)]
    [InlineData(ModelProvisioningState.Pending)]
    [InlineData(ModelProvisioningState.Failed)]
    public void NoModelPulling_DrawsNoChipAtAll(ModelProvisioningState state)
    {
        Publish(Model("qwen3-embedding", state));

        Render<ModelPullChip>().Markup.Trim().ShouldBeEmpty();
    }

    [Fact]
    public void APullOllamaReportsATotalFor_NamesTheModelAndItsFigure()
    {
        Publish(Model("qwen3-embedding", ModelProvisioningState.Pulling, percent: 42));

        var chip = Render<ModelPullChip>();

        chip.Find("div.model-pull").TextContent.ShouldContain("qwen3-embedding");
        chip.Find("span.model-pull-percent").TextContent.ShouldBe("42%");
    }

    /// <summary>
    /// Ollama does not always report a total, and the chip claims no figure when it has none —
    /// the same honesty the Storage tile keeps about emptiness. The model's name still shows,
    /// because "something is being pulled" is the part a first run is waiting on.
    /// </summary>
    [Fact]
    public void APullWithNoTotalReported_NamesTheModelWithoutInventingAFigure()
    {
        Publish(Model("qwen3", ModelProvisioningState.Pulling));

        var chip = Render<ModelPullChip>();

        chip.Find("div.model-pull").TextContent.ShouldContain("qwen3");
        chip.FindAll("span.model-pull-percent").ShouldBeEmpty();
        chip.FindAll("span.model-pull-track").ShouldBeEmpty();
    }
}
