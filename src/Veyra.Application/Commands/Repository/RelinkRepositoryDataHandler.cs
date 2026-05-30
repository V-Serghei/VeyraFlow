using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Recovery;

namespace Veyra.Application.Commands.Repository;

public sealed class RelinkRepositoryDataHandler(
    IRepositoryRecoveryService recovery,
    ILogger<RelinkRepositoryDataHandler> log)
    : IRequestHandler<RelinkRepositoryDataCommand, OperationResult<RepositoryRecoveryResultDto>>
{
    public async Task<OperationResult<RepositoryRecoveryResultDto>> Handle(
        RelinkRepositoryDataCommand request,
        CancellationToken ct)
    {
        try
        {
            var result = await recovery.RelinkRepositoryAsync(request.RepositoryId, ct);
            return OperationResult<RepositoryRecoveryResultDto>.Ok(result);
        }
        catch (OperationCanceledException)
        {
            return OperationResult<RepositoryRecoveryResultDto>.Fail("Relink operation was cancelled.");
        }
        catch (Exception ex)
        {
            log.LogError(ex,
                "Relink repository command failed. RepositoryId {RepositoryId}",
                request.RepositoryId);
            return OperationResult<RepositoryRecoveryResultDto>.Fail(ex.Message);
        }
    }
}

