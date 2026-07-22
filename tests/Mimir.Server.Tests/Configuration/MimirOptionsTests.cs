using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;

namespace Mimir.Server.Tests.Configuration;

/// <summary>
/// The §11 knob table is normative: every default asserted here is quoted from the spec.
/// </summary>
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
        // Compose passes knobs as env vars; the `__` separator must reach the same options.
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
        // Head plus tail is what survives the cap; a combination that exceeds it would make the
        // truncator emit more than the cap allows and the marker lie about what was dropped.
        // The near-int.MaxValue rows would wrap int addition around the check entirely — and
        // then index far outside the payload at truncation time.
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
