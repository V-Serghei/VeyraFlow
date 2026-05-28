using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;

namespace Veyra.Application.Commands.Repository;

public sealed class AddDirectoryAndCreateRepositoryHandler(
    ISetupRepository setup,
    IRepositoryRepository repo,
    IRepositoryScanner scanner,
    INativeSetupApplier native,
    ILogger<AddDirectoryAndCreateRepositoryHandler> log)
    : IRequestHandler<AddDirectoryAndCreateRepositoryCommand, OperationResult<int>>
{
    public async Task<OperationResult<int>> Handle(AddDirectoryAndCreateRepositoryCommand request, CancellationToken ct)
    {
        try
        {
            await setup.AddWatchedDirectoryAsync(request.DirectoryPath, ct);
            await repo.EnsureRepositoriesForAllDirectoriesAsync(ct);

            var all = await repo.GetAllRepositoriesAsync(ct);
            var match = all.FirstOrDefault(r =>
                r.DirectoryPath.Equals(request.DirectoryPath, StringComparison.OrdinalIgnoreCase));

            if (match is null)
                return OperationResult<int>.Fail("Directory was added but repository creation failed.");

            if (!string.IsNullOrWhiteSpace(request.RepositoryName))
                await repo.UpdateRepositoryAsync(match.Id, request.RepositoryName, null, ct);

            var scanResult = await InitialSnapshotCreation.RunWithBoundedRetryAsync(
                scanner,
                match.Id,
                null,
                log,
                ct);

            await native.ApplySetupAsync(ct);

            var snapshotStatus = InitialSnapshotCreation.ResolveSnapshotStatus(scanResult);
            if (InitialSnapshotCreation.RequiresUserRetry(scanResult))
            {
                log.LogWarning(
                    "Repository created but initial snapshot requires retry. RepositoryId {RepositoryId}. Path {Path}. Files {Files}. NoChanges {NoChanges}. ScanInProgress {ScanInProgress}. BusyFiles {BusyFiles}",
                    match.Id,
                    request.DirectoryPath,
                    scanResult.FileEntries,
                    scanResult.NoChangesDetected,
                    scanResult.SkippedBecauseScanInProgress,
                    scanResult.BusyFilesCount);
            }

            log.LogInformation(
                "Added directory and created repository for {Path}. InitialSnapshotStatus {InitialSnapshotStatus}",
                request.DirectoryPath,
                snapshotStatus);
            return OperationResult<int>.Ok(
                match.Id,
                $"Repository created. Directory scan matched {scanResult.FileEntries} file(s) and {scanResult.DirectoryEntries} folder(s) using {match.LinkedFormats.Count} selected format(s). Initial versioned snapshot {snapshotStatus}. Busy files: {scanResult.BusyFilesCount}.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to add directory and create repository");
            return OperationResult<int>.Fail(ex.Message);
        }
    }
}
