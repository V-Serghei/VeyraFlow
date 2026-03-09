using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Setup;

public interface IRepositoryRepository
{
    Task<int> CreateRepositoryAsync(string name, string? description, int directoryId, CancellationToken ct = default);
    Task UpdateRepositoryAsync(int id, string name, string? description, CancellationToken ct = default);
    Task UpdateRepositoryAsync(
        int id,
        string name,
        string? description,
        RepositoryRetentionPolicyDto retentionPolicy,
        CancellationToken ct = default);
    Task DeleteRepositoryAsync(int id, CancellationToken ct = default);
    Task RestoreRepositoryAsync(int id, CancellationToken ct = default);

    Task<RepositoryDto?> GetRepositoryByIdAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<RepositoryDto>> GetAllRepositoriesAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates a repository for each directory that doesn't already have one.
    /// Used after initial setup wizard.
    /// </summary>
    Task EnsureRepositoriesForAllDirectoriesAsync(CancellationToken ct = default);
}

