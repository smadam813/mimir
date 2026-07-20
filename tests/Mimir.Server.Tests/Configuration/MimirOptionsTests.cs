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

    [Fact]
    public void HeadPlusTailBeyondTheCap_FailsValidation()
    {
        // Head plus tail is what survives the cap; a combination that exceeds it would make the
        // truncator emit more than the cap allows and the marker lie about what was dropped.
        Should.Throw<OptionsValidationException>(() => Resolve<CaptureOptions>(new Dictionary<string, string?>
        {
            ["Mimir:Capture:PayloadHeadBytes"] = "4000",
            ["Mimir:Capture:PayloadTailBytes"] = "2000",
        }));
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
