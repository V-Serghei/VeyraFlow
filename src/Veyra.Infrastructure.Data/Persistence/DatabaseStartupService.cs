using Veyra.Application.Abstractions.Setup;

namespace Veyra.Infrastructure.Data.Persistence;

public sealed class DatabaseStartupService(VeyraDbContext db) : IDatabaseStartupService
{
    public void Initialize()
        => DatabaseStartupBootstrapper.Initialize(db);
}
