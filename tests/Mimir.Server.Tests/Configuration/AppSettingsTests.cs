using Microsoft.Extensions.Configuration;
using Mimir.Server.Configuration;

namespace Mimir.Server.Tests.Configuration;

/// <summary>
/// appsettings.json restates the §11 defaults so the shipped config documents itself. That is only
/// safe while the two agree — this is what stops them drifting apart.
/// </summary>
public class AppSettingsTests
{
    private static readonly IConfiguration AppSettings =
        new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();

    [Fact]
    public void ShippedServerSection_MatchesTheCodeDefaults()
    {
        var shipped = AppSettings.GetSection(ServerOptions.SectionName).Get<ServerOptions>().ShouldNotBeNull();

        shipped.Port.ShouldBe(new ServerOptions().Port);
    }

    [Fact]
    public void ShippedModelsSection_MatchesTheCodeDefaults()
    {
        var shipped = AppSettings.GetSection(ModelOptions.SectionName).Get<ModelOptions>().ShouldNotBeNull();
        var expected = new ModelOptions();

        shipped.Endpoint.ShouldBe(expected.Endpoint);
        shipped.Distiller.ShouldBe(expected.Distiller);
        shipped.Embedding.ShouldBe(expected.Embedding);
        shipped.EmbeddingDimensions.ShouldBe(expected.EmbeddingDimensions);
    }
}
