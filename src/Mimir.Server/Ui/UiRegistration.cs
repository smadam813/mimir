namespace Mimir.Server.Ui;

public static class UiRegistration
{
    /// <summary>Spec §8: the services the Blazor surfaces read and mutate through.</summary>
    public static IServiceCollection AddMimirUi(this IServiceCollection services)
    {
        services.AddSingleton<EpisodeBrowser>();
        return services;
    }
}
