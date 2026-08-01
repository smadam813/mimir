using Microsoft.Extensions.DependencyInjection;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

public class UiRegistrationTests
{
    [Fact]
    public void EveryUiService_IsRegisteredExactlyOnce()
    {
        var services = new ServiceCollection().AddMimirUi();

        var duplicated = services
            .GroupBy(descriptor => descriptor.ServiceType)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key.Name)
            .ToList();

        duplicated.ShouldBeEmpty();
    }

    /// <summary>
    /// A Blazor Server scope is one circuit, and the header's box is claimed per circuit (#94):
    /// the term in it belongs to the curator typing it rather than to the install, so two browser
    /// tabs are two curators. The browsers beside it are the opposite question — stateless readers
    /// over their own short-lived contexts — and are asserted here so the odd one out cannot
    /// quietly become the convention.
    /// </summary>
    [Fact]
    public void TheSearchBox_IsScopedToOneCircuit_WhileTheBrowsersAreNot()
    {
        var services = new ServiceCollection().AddMimirUi();

        Lifetime<SurfaceSearch>(services).ShouldBe(ServiceLifetime.Scoped);
        Lifetime<ChassisBrowser>(services).ShouldBe(ServiceLifetime.Singleton);
        Lifetime<EpisodeBrowser>(services).ShouldBe(ServiceLifetime.Singleton);
        Lifetime<WisdomBrowser>(services).ShouldBe(ServiceLifetime.Singleton);
        Lifetime<InjectionBrowser>(services).ShouldBe(ServiceLifetime.Singleton);
    }

    private static ServiceLifetime Lifetime<T>(IServiceCollection services)
        => services.Single(descriptor => descriptor.ServiceType == typeof(T)).Lifetime;
}
