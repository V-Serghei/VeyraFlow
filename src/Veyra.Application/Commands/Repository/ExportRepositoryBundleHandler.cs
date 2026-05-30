using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Bundles;

namespace Veyra.Application.Commands.Repository;

public sealed class ExportRepositoryBundleHandler(
    IRepositoryBundleService bundles,
    ILogger<ExportRepositoryBundleHandler> log)
    : IRequestHandler<ExportRepositoryBundleCommand, OperationResult<RepositoryBundleExportResultDto>>
{
    public async Task<OperationResult<RepositoryBundleExportResultDto>> Handle(
        ExportRepositoryBundleCommand request,
        CancellationToken ct)
    {
        try
        {
            var result = await bundles.ExportRepositoryAsync(
                request.RepositoryId,
                request.BundlePath,
                ct);

            return OperationResult<RepositoryBundleExportResultDto>.Ok(result);
        }
        catch (Exception ex)
        {
            log.LogError(ex,
                "Failed to export repository bundle. RepositoryId {RepositoryId}. Path {BundlePath}",
                request.RepositoryId,
                request.BundlePath);

            return OperationResult<RepositoryBundleExportResultDto>.Fail(ex.Message);
        }
    }
}
