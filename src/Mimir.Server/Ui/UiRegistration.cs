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
        // Scoped, unlike the browsers: a Blazor Server scope is one circuit, and the header's
        // search term belongs to the curator typing it rather than to the install.
        services.AddScoped<SurfaceSearch>();
        return services;
    }
}
