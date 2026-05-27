using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;

namespace Veyra.Application.Commands.Repository;

public sealed class ReindexRepositoryDataHandler(
    IRepositoryRecoveryService recovery,
    ILogger<ReindexRepositoryDataHandler> log)
    : IRequestHandler<ReindexRepositoryDataCommand, OperationResult<RepositoryRecoveryResultDto>>
{
    public async Task<OperationResult<RepositoryRecoveryResultDto>> Handle(
        ReindexRepositoryDataCommand request,
        CancellationToken ct)
    {
        try
        {
            var result = await recovery.ReindexRepositoryAsync(request.RepositoryId, ct);
            return OperationResult<RepositoryRecoveryResultDto>.Ok(result);
        }
        catch (OperationCanceledException)
        {
            return OperationResult<RepositoryRecoveryResultDto>.Fail("Reindex operation was cancelled.");
        }
        catch (Exception ex)
        {
            log.LogError(ex,
                "Reindex repository command failed. RepositoryId {RepositoryId}",
                request.RepositoryId);
            return OperationResult<RepositoryRecoveryResultDto>.Fail(ex.Message);
        }
    }
}

