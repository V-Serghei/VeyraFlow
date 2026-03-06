using System;
using System.IO;
using Microsoft.Data.Sqlite;
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

        using var sp = services.BuildServiceProvider();
        var db = sp.GetRequiredService<VeyraDbContext>();

        Assert.NotNull(db);
        Assert.NotNull(db.FileSnapshots);
    }

    [Fact]
    public void Database_Migrate_CreatesSchema()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"veyra_test_{Guid.NewGuid():N}.db");
        var cs = $"Data Source={dbPath}";

        try
        {
            var services = new ServiceCollection();
            services.AddInfrastructureData(cs);

            using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VeyraDbContext>();

            db.Database.Migrate();

            var exists = db.Database.CanConnect();
            Assert.True(exists);
        }
        finally
        {
            SqliteConnection.ClearAllPools();

            if (File.Exists(dbPath))
            {
                for (var i = 0; i < 5; i++)
                {
                    try
                    {
                        File.Delete(dbPath);
                        break;
                    }
                    catch (IOException) when (i < 4)
                    {
                        System.Threading.Thread.Sleep(50);
                    }
                }
            }
        }
    }
}
