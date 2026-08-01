namespace Mimir.Server.Ui;

public static class UiRegistration
{
    public static IServiceCollection AddMimirUi(this IServiceCollection services)
    {
        services.AddSingleton<ChassisBrowser>();
        services.AddSingleton<EpisodeBrowser>();
        services.AddSingleton<WisdomBrowser>();
        services.AddSingleton<InjectionBrowser>();
        services.AddScoped<SurfaceSearch>();
        return services;
    }
}
