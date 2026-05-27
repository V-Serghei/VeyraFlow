using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;

namespace Veyra.Application.Commands.Repository;

public sealed class ScanRepositoryHandler(
    IRepositoryScanner scanner,
    IServiceScopeFactory scopeFactory,
    ILogger<ScanRepositoryHandler> log)
    : IRequestHandler<ScanRepositoryCommand, OperationResult<RepositoryScanResultDto>>
{
    public async Task<OperationResult<RepositoryScanResultDto>> Handle(ScanRepositoryCommand request, CancellationToken ct)
    {
        try
        {
            log.LogInformation("Manual scan requested for repository {RepositoryId}", request.RepositoryId);

            var result = await scanner.ScanRepositoryAsync(request.RepositoryId, request.Progress, request.Options, ct);

            if (result.SkippedBecauseScanInProgress)
            {
                log.LogInformation(
                    "Manual scan deferred because another scan is already active. RepositoryId {RepositoryId}. Trigger {Trigger}",
                    request.RepositoryId,
                    result.Trigger);
                return OperationResult<RepositoryScanResultDto>.Ok(result);
            }

            log.LogInformation(
                "Manual scan finished for repository {RepositoryId}. Files {Files}. Entries {Entries}. Trigger {Trigger}",
                request.RepositoryId,
                result.FileEntries,
                result.TotalEntries,
                result.Trigger);

            if (request.Options?.SaveFileVersions == true && result.SnapshotCreated && !result.HasBusyFiles)
            {
                QueueCloudSyncInBackground(request.RepositoryId);
            }
            else if (request.Options?.SaveFileVersions == true && result.HasBusyFiles)
            {
                log.LogWarning(
                    "Skipping cloud sync after scan because snapshot has busy-file warnings. RepositoryId {RepositoryId}. BusyFiles {BusyFiles}",
                    request.RepositoryId,
                    result.BusyFilesCount);
            }

            return OperationResult<RepositoryScanResultDto>.Ok(result);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to scan repository {RepositoryId}", request.RepositoryId);
            return OperationResult<RepositoryScanResultDto>.Fail(ex.Message);
        }
    }

    private void QueueCloudSyncInBackground(int repositoryId)
    {
        log.LogInformation(
            "Queued background cloud sync after scan. RepositoryId {RepositoryId}",
            repositoryId);

        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var backgroundSync = scope.ServiceProvider.GetRequiredService<IRepositoryCloudSyncOrchestrator>();
                await backgroundSync.TryPushLatestSnapshotAsync(repositoryId, CancellationToken.None);
            }
            catch (Exception syncEx)
            {
                log.LogWarning(
                    syncEx,
                    "Background cloud sync after scan failed for repository {RepositoryId}",
                    repositoryId);
            }
        });
    }
}
