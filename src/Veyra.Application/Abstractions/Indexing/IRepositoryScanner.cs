namespace Veyra.Application.Abstractions.Indexing;

public interface IRepositoryScanner
{
    Task ScanRepositoryAsync(int repositoryId, CancellationToken ct = default);
    Task ScanAllRepositoriesAsync(CancellationToken ct = default);
}
