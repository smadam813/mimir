using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// The Merge Gate's pre-flight guards — the paths that answer before the gate embeds, opens a
/// context, or takes its lock. Deliberately outside <see cref="PostgresTestBase"/>: built over
/// <see cref="DisconnectedContextFactory"/>, so deleting a guard goes red on every machine instead
/// of disappearing into the harness's no-Postgres skip.
/// </summary>
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
