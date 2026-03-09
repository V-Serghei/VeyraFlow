using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;

namespace Veyra.Application.Commands.Repository;

public sealed class ImportRepositoryBundleHandler(
    IRepositoryBundleService bundles,
    ILogger<ImportRepositoryBundleHandler> log)
    : IRequestHandler<ImportRepositoryBundleCommand, OperationResult<RepositoryBundleImportResultDto>>
{
    public async Task<OperationResult<RepositoryBundleImportResultDto>> Handle(
        ImportRepositoryBundleCommand request,
        CancellationToken ct)
    {
        try
        {
            var result = await bundles.ImportRepositoryAsync(
                request.BundlePath,
                request.TargetDirectoryPath,
                request.RepositoryNameOverride,
                ct);

            return OperationResult<RepositoryBundleImportResultDto>.Ok(result);
        }
        catch (Exception ex)
        {
            log.LogError(ex,
                "Failed to import repository bundle. Bundle {BundlePath}. Target {TargetDirectoryPath}",
                request.BundlePath,
                request.TargetDirectoryPath);

            return OperationResult<RepositoryBundleImportResultDto>.Fail(ex.Message);
        }
    }
}
