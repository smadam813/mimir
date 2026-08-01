using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using OllamaSharp;

namespace Mimir.Server.Models;

public static class ModelRegistration
{
    public static IServiceCollection AddMimirModelClients(this IServiceCollection services)
    {
        services.AddSingleton<IOllamaApiClient>(provider => ClientFor(provider, options => options.Distiller));
        services.AddChatClient(provider => (IChatClient)provider.GetRequiredService<IOllamaApiClient>());
        services.AddEmbeddingGenerator(provider =>
            (IEmbeddingGenerator<string, Embedding<float>>)ClientFor(provider, options => options.Embedding));

        services.AddSingleton<IModelCatalog, OllamaModelCatalog>();
        services.AddSingleton<ModelProvisioner>();
        services.AddHostedService<ModelProvisioningService>();

        return services;
    }

    private static OllamaApiClient ClientFor(IServiceProvider provider, Func<ModelOptions, string> selectModel)
    {
        var options = provider.GetRequiredService<IOptions<ModelOptions>>().Value;
        return new OllamaApiClient(options.Endpoint, selectModel(options));
    }
}
