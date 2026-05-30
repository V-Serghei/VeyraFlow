using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Recovery;

namespace Veyra.Application.Commands.Repository;

public sealed class RepairRepositoryDataHandler(
    IRepositoryRecoveryService recovery,
    ILogger<RepairRepositoryDataHandler> log)
    : IRequestHandler<RepairRepositoryDataCommand, OperationResult<RepositoryRecoveryResultDto>>
{
    public async Task<OperationResult<RepositoryRecoveryResultDto>> Handle(
        RepairRepositoryDataCommand request,
        CancellationToken ct)
    {
        try
        {
            var result = await recovery.RepairRepositoryAsync(
                request.RepositoryId,
                request.RepairMissingBlocksFromCloud,
                ct);

            return OperationResult<RepositoryRecoveryResultDto>.Ok(result);
        }
        catch (OperationCanceledException)
        {
            return OperationResult<RepositoryRecoveryResultDto>.Fail("Repair operation was cancelled.");
        }
        catch (Exception ex)
        {
            var failureMessage = ex.InnerException is not null
                ? $"{ex.Message} Inner: {ex.InnerException.Message}"
                : ex.Message;
            log.LogError(ex,
                "Repair repository command failed. RepositoryId {RepositoryId}",
                request.RepositoryId);
            return OperationResult<RepositoryRecoveryResultDto>.Fail(failureMessage);
        }
    }
}
