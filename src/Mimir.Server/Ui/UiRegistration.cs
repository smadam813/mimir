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
        // Scoped, unlike the browsers: a Blazor Server scope is one circuit, the header's box is
        // claimed per circuit (#94), and the term in it belongs to the curator typing it rather
        // than to the install. Registered once — #91 and #94 landed this same line in parallel and
        // both survived, which is what UiRegistrationTests now watches for.
        services.AddScoped<SurfaceSearch>();
        return services;
    }
}
