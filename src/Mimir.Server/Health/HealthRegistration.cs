namespace Mimir.Server.Health;

public static class HealthRegistration
{
    public static IServiceCollection AddMimirHealth(this IServiceCollection services)
        => services.AddSingleton<IHealthState, HealthState>();
}
