using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;

namespace Veyra.Application.Commands.Repository;

public sealed class ScanRepositoryHandler(
    IRepositoryScanner scanner,
    ILogger<ScanRepositoryHandler> log)
    : IRequestHandler<ScanRepositoryCommand, OperationResult<RepositoryScanResultDto>>
{
    public async Task<OperationResult<RepositoryScanResultDto>> Handle(ScanRepositoryCommand request, CancellationToken ct)
    {
        try
        {
            var result = await scanner.ScanRepositoryAsync(request.RepositoryId, request.Progress, ct);
            return OperationResult<RepositoryScanResultDto>.Ok(result);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to scan repository {RepositoryId}", request.RepositoryId);
            return OperationResult<RepositoryScanResultDto>.Fail(ex.Message);
        }
    }
}
