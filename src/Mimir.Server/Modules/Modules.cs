using Mimir.Server.Capture;
using Mimir.Server.Distillation;
using Mimir.Server.Harvest;
using Mimir.Server.Recall;

namespace Mimir.Server.Modules;

/// <summary>
/// Spec §4: the passive, always-on recording of sessions into Episodes. Capture is dumb — no
/// judgment, no models, never blocks a session.
/// </summary>
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

/// <summary>
/// Spec §5: one-way ingestion of Claude Code's built-in auto-memory from the read-only
/// <c>/harvest</c> mount. Mimir never writes back (ADR-0002).
/// </summary>
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

/// <summary>
/// Spec §6: turning Sealed Episodes into Wisdom candidates, and the Merge Gate that is the single
/// write-time entry point to the Wisdom tier. Runs off every session hot path (ADR-0004).
/// </summary>
internal sealed class DistillationModule : IMimirModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<MergeGate>();
        services.AddScoped<IMergeArbiter, MergeArbiter>();
        services.AddScoped<ContestedSweep>();
        services.AddScoped<EpisodeDistiller>();
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

/// <summary>
/// Spec §7: the three lanes memories reach a session through — Brief, Prompt and MCP. Everything
/// here fails open.
/// </summary>
internal sealed class RecallModule : IMimirModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
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
