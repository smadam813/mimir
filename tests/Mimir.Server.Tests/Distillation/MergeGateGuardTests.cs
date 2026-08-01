using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;

namespace Mimir.Server.Tests.Distillation;

public sealed class MergeGateGuardTests
{
    [Fact]
    public async Task ABlankEdit_ReturnsBeforeTheGateOpensAnything()
    {
        var embeddings = new FakeEmbeddings();
        var gate = new MergeGate(
            new DisconnectedContextFactory(),
            embeddings,
            Options.Create(new SearchOptions()),
            new FakeArbiter(),
            Options.Create(new DistillationOptions()),
            TimeProvider.System);

        await gate.EditAsync(Guid.NewGuid(), "   \t\n ", TestContext.Current.CancellationToken);

        embeddings.Batches.ShouldBe(0, "a blank edit is not even worth an embedding");
    }
}
