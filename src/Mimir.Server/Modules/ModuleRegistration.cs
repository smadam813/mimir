namespace Mimir.Server.Modules;

internal static class ModuleRegistration
{
    private static readonly IMimirModule[] Modules =
    [
        new CaptureModule(),
        new HarvestModule(),
        new DistillationModule(),
        new RecallModule(),
    ];

    public static IServiceCollection AddMimirModules(this IServiceCollection services, IConfiguration configuration)
    {
        foreach (var module in Modules)
        {
            module.AddServices(services, configuration);
        }

        return services;
    }

    public static IEndpointRouteBuilder MapMimirModules(this IEndpointRouteBuilder endpoints)
    {
        foreach (var module in Modules)
        {
            module.MapEndpoints(endpoints);
        }

        return endpoints;
    }
}
