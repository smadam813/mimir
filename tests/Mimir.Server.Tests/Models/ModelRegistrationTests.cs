using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Models;
using OllamaSharp;
using OllamaSharp.Models;

namespace Mimir.Server.Tests.Models;

/// <summary>
/// What <c>AddMimirModelClients</c> composes, and the one pure calculation the catalog does.
/// Constructing an <c>OllamaApiClient</c> opens no connection, so nothing here needs Ollama.
/// </summary>
public sealed class ModelRegistrationTests
{
    [Fact]
    public void ChatAndEmbedding_EachGetTheirOwnClient()
    {
        using var provider = Provider();

        var chat = provider.GetRequiredService<IChatClient>().GetService(typeof(IOllamaApiClient));
        var embedding = provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()
            .GetService(typeof(IOllamaApiClient));

        var options = new ModelOptions();
        ((IOllamaApiClient)chat!).SelectedModel.ShouldBe(options.Distiller);
        ((IOllamaApiClient)embedding!).SelectedModel.ShouldBe(options.Embedding);
        chat.ShouldNotBeSameAs(
            embedding, "one client carries one selected model, so two models need two clients");
    }

    [Fact]
    public void Provisioning_IsAHostedBackgroundService_SoStartupNeverWaitsOnADownload()
    {
        var services = Registrations();

        services.ShouldContain(
            d => d.ServiceType == typeof(IHostedService)
                && d.ImplementationType == typeof(ModelProvisioningService));
        typeof(ModelProvisioningService).IsSubclassOf(typeof(BackgroundService)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(1000, 250, 25)]
    [InlineData(1000, 1000, 100)]
    public void APullsPercentage_IsReportedOnlyWhereAByteTotalIs(long total, long completed, int? expected)
        => OllamaModelCatalog.ToPercent(new PullModelResponse { Total = total, Completed = completed })
            .ShouldBe(expected);

    private static ServiceProvider Provider() => Registrations().BuildServiceProvider();

    private static ServiceCollection Registrations()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ModelOptions()));
        services.AddMimirModelClients();
        return services;
    }
}
