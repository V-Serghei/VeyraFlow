using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Veyra.Infrastructure.Data.Persistence;

namespace Veyra.Infrastructure.Data.Tests;

public class DbContextTests
{
    [Fact]
    public void DbContext_CanBeCreated()
    {
        var services = new ServiceCollection();
        services.AddInfrastructureData("Data Source=:memory:");

        var serviceProvider = services.BuildServiceProvider();
        var dbContext = serviceProvider.GetRequiredService<VeyraDbContext>();

        Assert.NotNull(dbContext);
        Assert.NotNull(dbContext.FileSnapshots);
    }

    [Fact]
    public void EnableWalMode_DoesNotThrow()
    {
        var connectionString = "Data Source=:memory:";
        var exception = Record.Exception(() => DependencyInjection.EnableWalMode(connectionString));
        
        // WAL mode might not work on in-memory databases, but shouldn't throw
        Assert.Null(exception);
    }
}
