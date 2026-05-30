using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Integrity;

namespace Veyra.Application.Commands.Repository;

public sealed class RunRepositoryIntegrityCheckHandler(
    IRepositoryIntegrityService integrity,
    ILogger<RunRepositoryIntegrityCheckHandler> log)
    : IRequestHandler<RunRepositoryIntegrityCheckCommand, OperationResult<RepositoryIntegrityRunResultDto>>
{
    public async Task<OperationResult<RepositoryIntegrityRunResultDto>> Handle(
        RunRepositoryIntegrityCheckCommand request,
        CancellationToken ct)
    {
        try
        {
            if (request.RepositoryId <= 0)
                return OperationResult<RepositoryIntegrityRunResultDto>.Fail("Repository id must be positive.");

            var result = await integrity.VerifyRepositoryAsync(
                request.RepositoryId,
                request.RepairFromCloud,
                request.MaxIssueSamples,
                request.Progress,
                ct);

            return OperationResult<RepositoryIntegrityRunResultDto>.Ok(result);
        }
        catch (OperationCanceledException)
        {
            log.LogWarning(
                "Repository integrity check cancelled. RepositoryId {RepositoryId}. Repair {Repair}",
                request.RepositoryId,
                request.RepairFromCloud);

            return OperationResult<RepositoryIntegrityRunResultDto>.Fail("Integrity check was cancelled.");
        }
        catch (Exception ex)
        {
            log.LogError(
                ex,
                "Repository integrity check failed. RepositoryId {RepositoryId}. Repair {Repair}",
                request.RepositoryId,
                request.RepairFromCloud);

            return OperationResult<RepositoryIntegrityRunResultDto>.Fail(ex.Message);
        }
    }
}
