using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using OllamaSharp;

namespace Mimir.Server.Models;

public static class ModelRegistration
{
    /// <summary>
    /// Spec §2: every model call goes through the Microsoft.Extensions.AI abstractions. OllamaSharp
    /// is the implementation, and its native API — not the OpenAI-compatible surface — is what
    /// makes startup model provisioning possible.
    /// </summary>
    public static IServiceCollection AddMimirModelClients(this IServiceCollection services)
    {
        // An OllamaApiClient carries one selected model, so chat and embedding get an instance
        // each — the §11 distiller and embedding models respectively.
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
