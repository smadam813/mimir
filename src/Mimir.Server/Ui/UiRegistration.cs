namespace Mimir.Server.Ui;

public static class UiRegistration
{
    /// <summary>Spec §8: the services the Blazor surfaces read and mutate through.</summary>
    public static IServiceCollection AddMimirUi(this IServiceCollection services)
    {
        services.AddSingleton<ChassisBrowser>();
        services.AddSingleton<EpisodeBrowser>();
        services.AddSingleton<WisdomBrowser>();
        services.AddSingleton<InjectionBrowser>();
        // Scoped, unlike the browsers: it carries one circuit's search claim and term, not shared
        // read state.
        services.AddScoped<SurfaceSearch>();
        return services;
    }
}
