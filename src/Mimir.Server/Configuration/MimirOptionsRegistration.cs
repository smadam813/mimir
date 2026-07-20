using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mimir.Server.Configuration;

/// <summary>
/// The single place the spec §11 knob table is bound. Every knob is an options class with its
/// documented default baked in as a property initialiser, validated by data annotations and
/// checked at startup so a bad value fails the boot rather than surfacing later.
/// </summary>
/// <remarks>
/// Later tickets add their own §11 sections here — one options class, one <see cref="AddSection{TOptions}"/>
/// line. Nothing else needs to change.
/// </remarks>
public static class MimirOptionsRegistration
{
    public static IServiceCollection AddMimirOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSection<ServerOptions>(configuration, ServerOptions.SectionName);
        services.AddSection<ModelOptions>(configuration, ModelOptions.SectionName);
        services.AddSection<CaptureOptions>(configuration, CaptureOptions.SectionName);
        services.AddSection<HarvestOptions>(configuration, HarvestOptions.SectionName);
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
