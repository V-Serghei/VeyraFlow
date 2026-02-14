namespace Veyra.Application.Abstractions.Setup;

public interface ISetupRepository
{
    Task SaveInitialSetupAsync(IReadOnlyCollection<string> paths, IReadOnlyCollection<string> extensions, CancellationToken ct = default);
    Task ReplaceWatchedDirectoriesAsync(IReadOnlyCollection<string> paths, CancellationToken ct = default);
    Task DeleteWatchedDirectoriesAsync(IReadOnlyCollection<string> paths, CancellationToken ct = default);
    Task ReplaceTrackedExtensionsAsync(IReadOnlyCollection<string> extensions, CancellationToken ct = default);
    Task DeleteTrackedExtensionsAsync(IReadOnlyCollection<string> extensions, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetWatchedDirectoriesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetTrackedExtensionsAsync(CancellationToken ct = default);
}
