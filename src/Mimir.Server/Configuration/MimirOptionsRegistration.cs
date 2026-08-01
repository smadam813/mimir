using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mimir.Server.Configuration;

public static class MimirOptionsRegistration
{
    public static IServiceCollection AddMimirOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSection<ServerOptions>(configuration, ServerOptions.SectionName);
        services.AddSection<ModelOptions>(configuration, ModelOptions.SectionName);
        services.AddSection<CaptureOptions>(configuration, CaptureOptions.SectionName);
        services.AddSection<HarvestOptions>(configuration, HarvestOptions.SectionName);
        services.AddSection<SearchOptions>(configuration, SearchOptions.SectionName);
        services.AddSection<DistillationOptions>(configuration, DistillationOptions.SectionName);
        services.AddSection<RecallOptions>(configuration, RecallOptions.SectionName);
        return services;
    }

    private static void AddSection<TOptions>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName)
        where TOptions : class
        => services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
}
