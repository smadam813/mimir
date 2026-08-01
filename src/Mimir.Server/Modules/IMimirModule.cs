namespace Mimir.Server.Modules;

internal interface IMimirModule
{
    void AddServices(IServiceCollection services, IConfiguration configuration);

    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
