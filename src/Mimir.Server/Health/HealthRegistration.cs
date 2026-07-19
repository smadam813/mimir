namespace Mimir.Server.Health;

public static class HealthRegistration
{
    /// <summary>Spec §8: the health strip and the state it renders.</summary>
    public static IServiceCollection AddMimirHealth(this IServiceCollection services)
        => services.AddSingleton<IHealthState, HealthState>();
}
