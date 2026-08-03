using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;

namespace Mimir.Server.Tests.Configuration;

public class MimirOptionsTests
{
    [Fact]
    public void ServerPort_DefaultsTo6464()
    {
        var options = Resolve<ServerOptions>();

        options.Port.ShouldBe(6464);
    }

    [Fact]
    public void Models_DefaultToTheSpecdQwen3Pair()
    {
        var options = Resolve<ModelOptions>();

        options.Distiller.ShouldBe("qwen3:8b");
        options.Embedding.ShouldBe("qwen3-embedding:0.6b");
        options.EmbeddingDimensions.ShouldBe(1024);
    }

    [Fact]
    public void OptionsBindFromConfiguration()
    {
        var options = Resolve<ModelOptions>(new Dictionary<string, string?>
        {
            ["Mimir:Models:Distiller"] = "llama3:70b",
            ["Mimir:Models:Endpoint"] = "http://elsewhere:11434",
        });

        options.Distiller.ShouldBe("llama3:70b");
        options.Endpoint.ShouldBe(new Uri("http://elsewhere:11434"));
        options.Embedding.ShouldBe("qwen3-embedding:0.6b", "unset knobs keep their documented default");
    }

    [Fact]
    public void OptionsBindFromDoubleUnderscoreEnvironmentVariables()
    {
        const string variable = "Mimir__Models__Distiller";
        Environment.SetEnvironmentVariable(variable, "qwen3:14b");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

            Resolve<ModelOptions>(configuration).Distiller.ShouldBe("qwen3:14b");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Theory]
    [InlineData("Mimir:Server:Port", "0")]
    [InlineData("Mimir:Server:Port", "70000")]
    public void InvalidServerPort_FailsValidation(string key, string value)
        => Should.Throw<OptionsValidationException>(
            () => Resolve<ServerOptions>(new Dictionary<string, string?> { [key] = value }));

    [Theory]
    [InlineData("Mimir:Models:Distiller", "")]
    [InlineData("Mimir:Models:Embedding", "")]
    [InlineData("Mimir:Models:EmbeddingDimensions", "0")]
    public void InvalidModelOptions_FailValidation(string key, string value)
        => Should.Throw<OptionsValidationException>(
            () => Resolve<ModelOptions>(new Dictionary<string, string?> { [key] = value }));

    [Fact]
    public void PayloadCapKnobs_DefaultToTheSpecd4K3K1K()
    {
        var options = Resolve<CaptureOptions>();

        options.PayloadFieldCapBytes.ShouldBe(4096);
        options.PayloadHeadBytes.ShouldBe(3072);
        options.PayloadTailBytes.ShouldBe(1024);
    }

    [Theory]
    [InlineData("4000", "2000")]
    [InlineData("2147483647", "1")]
    [InlineData("1", "2147483647")]
    public void HeadPlusTailBeyondTheCap_FailsValidation(string head, string tail)
    {
        Should.Throw<OptionsValidationException>(() => Resolve<CaptureOptions>(new Dictionary<string, string?>
        {
            ["Mimir:Capture:PayloadHeadBytes"] = head,
            ["Mimir:Capture:PayloadTailBytes"] = tail,
        }));
    }

    [Fact]
    public void HarvestKnobs_DefaultToTheComposeMountAndTheSpecd5Minutes()
    {
        var options = Resolve<HarvestOptions>();

        options.Root.ShouldBe("/harvest");
        options.ScanInterval.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void DistillerKnobs_DefaultToTheSpecd6h24h1hAnd12K()
    {
        var options = Resolve<DistillationOptions>();

        options.SweepInterval.ShouldBe(TimeSpan.FromHours(6));
        options.CrashSealIdleAfter.ShouldBe(TimeSpan.FromHours(24));
        options.StaleRunningAfter.ShouldBe(TimeSpan.FromHours(1));
        options.ChunkTokens.ShouldBe(12_288);
    }

    [Theory]
    [InlineData("Mimir:Distillation:SweepInterval", "00:00:00")]
    [InlineData("Mimir:Distillation:StaleRunningAfter", "00:00:01")]
    [InlineData("Mimir:Distillation:CrashSealIdleAfter", "31.00:00:00")]
    [InlineData("Mimir:Distillation:ChunkTokens", "0")]
    [InlineData("Mimir:Distillation:ChunkTokens", "20000")] // past num_ctx: would overflow, not chunk
    public void InvalidDistillerKnobs_FailValidation(string key, string value)
        => Should.Throw<OptionsValidationException>(
            () => Resolve<DistillationOptions>(new Dictionary<string, string?> { [key] = value }));

    [Theory]
    [InlineData("Mimir:Harvest:Root", "")]
    [InlineData("Mimir:Harvest:ScanInterval", "00:00:00")]
    [InlineData("Mimir:Harvest:ScanInterval", "2.00:00:00")]
    public void InvalidHarvestOptions_FailValidation(string key, string value)
        => Should.Throw<OptionsValidationException>(
            () => Resolve<HarvestOptions>(new Dictionary<string, string?> { [key] = value }));

    [Fact]
    public void RecallKnobs_DefaultToTheSpecdBriefBudgetAndRankingFactors()
    {
        var options = Resolve<RecallOptions>();

        options.BriefBudgetChars.ShouldBe(4000);
        options.RecencyHalfLifeDays.ShouldBe(90);
        options.RecencyFloor.ShouldBe(0.3);
        options.SalienceBoost.ShouldBe(1.3);
    }

    [Theory]
    [InlineData("Mimir:Recall:BriefBudgetChars", "0")]
    [InlineData("Mimir:Recall:RecencyFloor", "1.5")]
    [InlineData("Mimir:Recall:SalienceBoost", "0.5")]
    public void InvalidRecallOptions_FailValidation(string key, string value)
        => Should.Throw<OptionsValidationException>(
            () => Resolve<RecallOptions>(new Dictionary<string, string?> { [key] = value }));

    /// <summary>
    /// Every test above validates on access, so all of them stay green with <c>ValidateOnStart</c>
    /// dropped from <c>AddMimirOptions</c>' shared <c>AddSection</c> helper — and a bad knob would
    /// then surface at whatever request first read it rather than refusing the boot. This is the
    /// one that needs a host: it is the host's start, not the resolve, that runs the validator.
    /// One section is enough because that helper is the single site all seven pass through.
    /// <para>
    /// An empty builder, not a defaulted one: the defaults would read the real
    /// <c>appsettings.json</c> and the environment beside this in-memory knob.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AnOutOfRangeKnob_RefusesTheBoot()
    {
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Mimir:Server:Port"] = "70000" });
        builder.Services.AddMimirOptions(builder.Configuration);
        using var host = builder.Build();

        var failure = await Should.ThrowAsync<OptionsValidationException>(
            async () => await host.StartAsync(TestContext.Current.CancellationToken));

        failure.OptionsType.ShouldBe(typeof(ServerOptions));
    }

    private static TOptions Resolve<TOptions>(Dictionary<string, string?>? settings = null)
        where TOptions : class
        => Resolve<TOptions>(new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build());

    private static TOptions Resolve<TOptions>(IConfiguration configuration)
        where TOptions : class
    {
        var provider = new ServiceCollection()
            .AddMimirOptions(configuration)
            .BuildServiceProvider();

        return provider.GetRequiredService<IOptions<TOptions>>().Value;
    }
}
