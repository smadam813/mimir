using Mimir.Server.Capture;
using Mimir.Server.Distillation;
using Mimir.Server.Harvest;
using Mimir.Server.Recall;

namespace Mimir.Server.Modules;

internal sealed class CaptureModule : IMimirModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IEpisodeFeed, EpisodeFeed>();
        services.AddScoped<ProjectResolver>();
        services.AddScoped<CaptureService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/capture/events", CaptureEndpoints.CaptureEventAsync);
        endpoints.MapPost("/api/hooks/user-prompt", CaptureEndpoints.UserPromptAsync);
        endpoints.MapPost("/api/hooks/session-start", CaptureEndpoints.SessionStartAsync);
    }
}

internal sealed class HarvestModule : IMimirModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<HarvestScanner>();
        services.AddScoped<HarvestConverter>();
        services.AddSingleton<IHarvestScanTrigger, HarvestScanTrigger>();
        services.AddHostedService<HarvesterService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}

internal sealed class DistillationModule : IMimirModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<MergeGate>();
        services.AddSingleton<IMergeArbiter, MergeArbiter>();
        services.AddScoped<ContestedSweep>();
        services.AddScoped<IEpisodeDistiller, EpisodeDistiller>();
        services.AddScoped<DistillationQueue>();
        services.AddScoped<DistillationRun>();
        services.AddScoped<DistillationSweep>();
        services.AddSingleton<IDistillationTrigger, DistillationTrigger>();
        services.AddHostedService<DistillerService>();
        services.AddHostedService<DistillationSweepService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}

internal sealed class RecallModule : IMimirModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<InjectionLog>();
        services.AddScoped<BriefService>();
        services.AddScoped<QueryRanking>();
        services.AddScoped<PromptRecallService>();
        services.AddScoped<McpProjects>();
        services.AddScoped<McpSearchService>();
        services.AddScoped<McpTimelineService>();
        services.AddScoped<McpRememberService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/mcp/search", McpEndpoints.SearchAsync);
        endpoints.MapPost("/api/mcp/timeline", McpEndpoints.TimelineAsync);
        endpoints.MapPost("/api/mcp/remember", McpEndpoints.RememberAsync);
    }
}
