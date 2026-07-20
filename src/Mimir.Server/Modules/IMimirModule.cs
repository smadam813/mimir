namespace Mimir.Server.Modules;

/// <summary>
/// A seam along the spec §2 pipeline. Mimir is one process — a modular monolith, no service split
/// in v1 — so a module is not a deployment unit; it is the boundary that keeps Capture, Harvest,
/// Distillation and Recall from reaching into each other's internals.
/// </summary>
/// <remarks>
/// A module owns its services and its HTTP surface, and nothing else registers on its behalf.
/// The modules are empty today: this ticket builds the chassis they slot into.
/// </remarks>
internal interface IMimirModule
{
    /// <summary>Registers everything this module needs. Called once, at startup.</summary>
    void AddServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>Maps this module's HTTP surface, if it has one.</summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
