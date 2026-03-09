using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;

namespace Veyra.Application.Commands.Repository;

public sealed class RunRepositoryRetentionHandler(
    IRepositoryRetentionService retention,
    ILogger<RunRepositoryRetentionHandler> log)
    : IRequestHandler<RunRepositoryRetentionCommand, OperationResult<RepositoryRetentionRunResultDto>>
{
    public async Task<OperationResult<RepositoryRetentionRunResultDto>> Handle(
        RunRepositoryRetentionCommand request,
        CancellationToken ct)
    {
        try
        {
            if (request.RepositoryId <= 0)
                return OperationResult<RepositoryRetentionRunResultDto>.Fail("Repository id must be positive.");

            var result = await retention.RunRetentionAsync(
                request.RepositoryId,
                request.DryRun,
                request.Progress,
                ct);

            return OperationResult<RepositoryRetentionRunResultDto>.Ok(result);
        }
        catch (OperationCanceledException)
        {
            log.LogInformation(
                "Repository retention cancelled. RepositoryId {RepositoryId}. DryRun {DryRun}",
                request.RepositoryId,
                request.DryRun);
            return OperationResult<RepositoryRetentionRunResultDto>.Fail("???????? ????????.");
        }
        catch (Exception ex)
        {
            log.LogError(
                ex,
                "Repository retention failed. RepositoryId {RepositoryId}. DryRun {DryRun}",
                request.RepositoryId,
                request.DryRun);

            return OperationResult<RepositoryRetentionRunResultDto>.Fail(ex.Message);
        }
    }
}
