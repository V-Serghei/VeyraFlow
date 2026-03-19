using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Veyra.Infrastructure.Data.Persistence;

public class VeyraDbContextFactory : IDesignTimeDbContextFactory<VeyraDbContext>
{
    public VeyraDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<VeyraDbContext>();
        optionsBuilder.UseSqlite("Data Source=veyra.db");

        return new VeyraDbContext(optionsBuilder.Options);
    }
}
