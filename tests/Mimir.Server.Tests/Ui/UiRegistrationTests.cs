using Microsoft.Extensions.DependencyInjection;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

/// <summary>
/// Spec §8's service registrations. Nothing is resolved here — descriptors are what is being
/// counted, so this runs everywhere rather than only where Postgres does.
/// </summary>
public class UiRegistrationTests
{
    [Fact]
    public void EveryUiService_IsRegisteredExactlyOnce()
    {
        // SurfaceSearch was registered twice: #91 and #94 ported a surface each in parallel and
        // both added the line, so the second descriptor won and the first became a dead one. It is
        // invisible at runtime — the container hands out the last registration and the surfaces
        // work — which is exactly why it survived two review rounds and needs a pin rather than a
        // reader (#101). Registering one service type twice on purpose is a real DI pattern — it is
        // how an IEnumerable<T> of implementations is composed — so if §8 ever wants one, this is
        // the test to amend rather than the shape to work around.
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
