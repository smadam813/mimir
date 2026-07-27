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
}
