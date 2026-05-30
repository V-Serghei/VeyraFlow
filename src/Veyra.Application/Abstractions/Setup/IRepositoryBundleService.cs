using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Bundles;

namespace Veyra.Application.Abstractions.Setup;

public interface IRepositoryBundleService
{
    Task<RepositoryBundleValidationResultDto> ValidateBundleAsync(
        string bundlePath,
        CancellationToken ct = default);

    Task<RepositoryBundleExportResultDto> ExportRepositoryAsync(
        int repositoryId,
        string bundlePath,
        CancellationToken ct = default);

    Task<RepositoryBundleImportResultDto> ImportRepositoryAsync(
        string bundlePath,
        string targetDirectoryPath,
        string? repositoryNameOverride = null,
        CancellationToken ct = default);
}
