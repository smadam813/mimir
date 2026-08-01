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
}
