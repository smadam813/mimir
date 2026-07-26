namespace Mimir.Server.Ui;

public static class UiRegistration
{
    /// <summary>Spec §8: the services the Blazor surfaces read and mutate through.</summary>
    public static IServiceCollection AddMimirUi(this IServiceCollection services)
    {
        services.AddSingleton<ChassisBrowser>();
        // Scoped, unlike the browsers: the header's search box is claimed per circuit (#94).
        services.AddScoped<SurfaceSearch>();
        services.AddSingleton<EpisodeBrowser>();
        services.AddSingleton<WisdomBrowser>();
        services.AddSingleton<InjectionBrowser>();
        return services;
    }
}
