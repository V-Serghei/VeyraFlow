using Microsoft.Extensions.DependencyInjection;

namespace Veyra.Application.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddApplication_RegistersServices()
    {
        var services = new ServiceCollection();
        services.AddApplication();

        var serviceProvider = services.BuildServiceProvider();
        Assert.NotNull(serviceProvider);
    }
}
