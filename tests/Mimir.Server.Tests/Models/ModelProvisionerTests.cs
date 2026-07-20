using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mimir.Contracts.Health;
using Mimir.Server.Configuration;
using Mimir.Server.Health;
using Mimir.Server.Models;

namespace Mimir.Server.Tests.Models;

public class ModelProvisionerTests
{
    private const string Distiller = "qwen3:8b";
    private const string Embedding = "qwen3-embedding:0.6b";

    private readonly FakeModelCatalog _catalog = new();
    private readonly HealthState _health = new();
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public async Task ModelsAlreadyPresent_GoStraightToReadyWithoutPulling()
    {
        _catalog.LocalModels.AddRange([Distiller, Embedding]);

        await Provision();

        _catalog.PullRequests.ShouldBeEmpty();
        Tile.State.ShouldBe(HealthTileState.Ready);
        Tile.Models.Select(m => m.State).ShouldAllBe(s => s == ModelProvisioningState.Ready);
    }

    [Fact]
    public async Task MissingModels_ArePulled()
    {
        await Provision();

        _catalog.PullRequests.ShouldBe([Distiller, Embedding]);
        Tile.State.ShouldBe(HealthTileState.Ready);
        Tile.Summary.ShouldContain(Distiller);
        Tile.Summary.ShouldContain(Embedding);
    }

    [Fact]
    public async Task PullProgress_ReachesTheTileAsItArrives()
    {
        _catalog.PullProgress[Distiller] =
        [
            new ModelPullProgress("pulling manifest", null),
            new ModelPullProgress("downloading", 25),
            new ModelPullProgress("downloading", 80),
        ];
        var seen = new List<(ModelProvisioningState State, int? Percent)>();
        using var _ = _health.Subscribe(snapshot =>
        {
            var model = snapshot.Ollama.Models.FirstOrDefault(m => m.Name == Distiller);
            if (model is not null)
            {
                seen.Add((model.State, model.PercentComplete));
            }
        });

        await Provision();

        // The demo in the acceptance criteria is watching this sequence on the tile.
        seen.ShouldContain((ModelProvisioningState.Pulling, 25));
        seen.ShouldContain((ModelProvisioningState.Pulling, 80));
        seen[^1].State.ShouldBe(ModelProvisioningState.Ready);
    }

    [Fact]
    public async Task WhilePulling_TheTileIsWorkingAndNamesWhatItIsDoing()
    {
        _catalog.PullProgress[Distiller] = [new ModelPullProgress("downloading", 42)];
        var summaries = new List<(HealthTileState State, string Summary)>();
        using var _ = _health.Subscribe(s => summaries.Add((s.Ollama.State, s.Ollama.Summary)));

        await Provision();

        summaries.ShouldContain(x => x.State == HealthTileState.Working && x.Summary.Contains("42%"));
    }

    [Theory]
    [InlineData("qwen3", "qwen3:latest")]
    [InlineData("qwen3:latest", "qwen3")]
    public async Task AnUntaggedModelMatchesItsLatestTag(string requested, string local)
    {
        _catalog.LocalModels.Add(local);

        await Provision(distiller: requested, embedding: local);

        _catalog.PullRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFailedPull_DegradesTheTileButStillProvisionsTheOthers()
    {
        _catalog.PullFailures[Distiller] = new InvalidOperationException("no space left on device");

        await Provision();

        _catalog.PullRequests.ShouldContain(Embedding, "one bad model must not abort the rest");
        Tile.State.ShouldBe(HealthTileState.Degraded);

        var failed = Tile.Models.Single(m => m.Name == Distiller);
        failed.State.ShouldBe(ModelProvisioningState.Failed);
        failed.Error.ShouldNotBeNull().ShouldContain("no space left on device");
        Tile.Models.Single(m => m.Name == Embedding).State.ShouldBe(ModelProvisioningState.Ready);
    }

    [Fact]
    public async Task AFailedPull_IsSurfacedWhileALaterModelIsStillPulling()
    {
        _catalog.PullFailures[Distiller] = new InvalidOperationException("no space left on device");
        _catalog.PullProgress[Embedding] = [new ModelPullProgress("downloading", 42)];
        var summaries = new List<(HealthTileState State, string Summary)>();
        using var _ = _health.Subscribe(s => summaries.Add((s.Ollama.State, s.Ollama.Summary)));

        await Provision();

        // A failed pull is terminal. Reporting Working until the last (multi-gigabyte) pull
        // finishes would hide it for minutes, so the tile carries both facts at once.
        summaries.ShouldContain(x =>
            x.State == HealthTileState.Degraded
            && x.Summary.Contains("42%")
            && x.Summary.Contains("1 of 2 models unavailable"));
    }

    [Fact]
    public async Task AnUnreachableOllama_IsRetriedUntilItAnswers()
    {
        _catalog
            .EnqueueListFailure(new HttpRequestException("connection refused"))
            .EnqueueListFailure(new HttpRequestException("connection refused"));
        _catalog.LocalModels.AddRange([Distiller, Embedding]);

        var provisioning = Provision();
        // Two retry intervals must elapse before the third (successful) list call happens.
        await AdvanceUntilComplete(provisioning);

        _catalog.ListCalls.ShouldBe(3);
        Tile.State.ShouldBe(HealthTileState.Ready);
    }

    [Fact]
    public async Task WhileOllamaIsUnreachable_TheTileSaysSoWithItsEndpoint()
    {
        _catalog.EnqueueListFailure(new HttpRequestException("connection refused"));
        var summaries = new List<(HealthTileState State, string Summary)>();
        using var _ = _health.Subscribe(s => summaries.Add((s.Ollama.State, s.Ollama.Summary)));

        await AdvanceUntilComplete(Provision());

        summaries.ShouldContain(x =>
            x.State == HealthTileState.Degraded && x.Summary.Contains("http://ollama-under-test:11434"));
    }

    [Fact]
    public async Task Cancellation_AbandonsTheRetryLoop()
    {
        using var cancellation = new CancellationTokenSource();
        _catalog.EnqueueListFailure(new HttpRequestException("connection refused"));

        var provisioning = Provision(cancellation.Token);
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(provisioning);
    }

    private OllamaTile Tile => _health.Current.Ollama;

    private Task Provision(string distiller = Distiller, string embedding = Embedding)
        => Provision(TestContext.Current.CancellationToken, distiller, embedding);

    private Task Provision(
        CancellationToken cancellationToken,
        string distiller = Distiller,
        string embedding = Embedding)
    {
        var options = new ModelOptions
        {
            Endpoint = new Uri("http://ollama-under-test:11434"),
            Distiller = distiller,
            Embedding = embedding,
        };

        var provisioner = new ModelProvisioner(
            _catalog,
            _health,
            Options.Create(options),
            _time,
            NullLogger<ModelProvisioner>.Instance);

        return provisioner.ProvisionAsync(cancellationToken);
    }

    /// <summary>Drives the fake clock forward until the provisioner stops waiting on a retry delay.</summary>
    private async Task AdvanceUntilComplete(Task provisioning)
    {
        for (var attempt = 0; attempt < 500 && !provisioning.IsCompleted; attempt++)
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
            _time.Advance(ModelProvisioner.RetryInterval);
        }

        await provisioning;
    }
}
