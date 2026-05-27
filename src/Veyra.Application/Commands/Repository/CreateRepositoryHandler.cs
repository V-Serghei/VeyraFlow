using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;

namespace Veyra.Application.Commands.Repository;

public sealed class CreateRepositoryHandler(
    IRepositoryRepository repo,
    ISetupRepository setup,
    IRepositoryScanner scanner,
    ILogger<CreateRepositoryHandler> log)
    : IRequestHandler<CreateRepositoryCommand, OperationResult<int>>
{
    public async Task<OperationResult<int>> Handle(CreateRepositoryCommand request, CancellationToken ct)
    {
        try
        {
            await setup.AddWatchedDirectoryAsync(request.DirectoryPath, ct);
            await repo.EnsureRepositoriesForAllDirectoriesAsync(ct);

            var all = await repo.GetAllRepositoriesAsync(ct);
            var match = all.FirstOrDefault(r =>
                r.DirectoryPath.Equals(request.DirectoryPath, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                if (!string.IsNullOrWhiteSpace(request.Name))
                    await repo.UpdateRepositoryAsync(match.Id, request.Name, request.Description, ct);

                var scanResult = await InitialSnapshotCreation.RunWithBoundedRetryAsync(
                    scanner,
                    match.Id,
                    null,
                    log,
                    ct);

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
                    "Repository ensured for {Path}: Id={Id}. InitialSnapshotStatus {InitialSnapshotStatus}",
                    request.DirectoryPath,
                    match.Id,
                    snapshotStatus);
                return OperationResult<int>.Ok(
                    match.Id,
                    $"Repository created. Directory scan matched {scanResult.FileEntries} file(s) and {scanResult.DirectoryEntries} folder(s) using {match.LinkedFormats.Count} selected format(s). Initial versioned snapshot {snapshotStatus}. Busy files: {scanResult.BusyFilesCount}.");
            }

            return OperationResult<int>.Fail("Failed to create repository for directory.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to create repository for {Path}", request.DirectoryPath);
            return OperationResult<int>.Fail(ex.Message);
        }
    }
}
