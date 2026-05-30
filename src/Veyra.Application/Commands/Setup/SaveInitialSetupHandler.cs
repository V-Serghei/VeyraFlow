using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Core;
using Veyra.Application.DTOs.Repository.Scanning;

namespace Veyra.Application.Commands.Setup;

public sealed class SaveInitialSetupHandler(
    ISetupRepository setupRepo,
    IRepositoryRepository repoRepo,
    IRepositoryScanner scanner,
    INativeSetupApplier native,
    ILogger<SaveInitialSetupHandler> log)
    : IRequestHandler<SaveInitialSetupCommand, OperationResult>
{
    public async Task<OperationResult> Handle(SaveInitialSetupCommand request, CancellationToken ct)
    {
        try
        {
            request.Progress?.Report(new RepositoryCreationProgressDto(
                "prepare",
                5,
                0,
                0,
                "Saving initial setup"));

            await setupRepo.SaveInitialSetupAsync(request.Directories, request.Extensions, ct);
            log.LogInformation(
                "Initial setup saved: {DirCount} dirs, {ExtCount} extensions, all linked",
                request.Directories.Count,
                request.Extensions.Count);

            request.Progress?.Report(new RepositoryCreationProgressDto(
                "prepare",
                15,
                0,
                0,
                "Creating repositories"));

            await repoRepo.EnsureRepositoriesForAllDirectoriesAsync(ct);
            log.LogInformation("Repositories ensured for all directories");

            if (!string.IsNullOrWhiteSpace(request.RepositoryName) && request.Directories.Count == 1)
            {
                var all = await repoRepo.GetAllRepositoriesAsync(ct);
                var match = all.FirstOrDefault(r =>
                    r.DirectoryPath.Equals(
                        request.Directories.First(),
                        StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                {
                    await repoRepo.UpdateRepositoryAsync(match.Id, request.RepositoryName, null, ct);
                    log.LogInformation("Repository renamed to {Name}", request.RepositoryName);
                }
            }

            var allRepositories = await repoRepo.GetAllRepositoriesAsync(ct);
            var selectedDirectories = request.Directories
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var targetRepositories = allRepositories
                .Where(repo => selectedDirectories.Contains(repo.DirectoryPath))
                .ToList();

            var totalRepositories = Math.Max(targetRepositories.Count, 1);
            var scannedFiles = 0;

            request.Progress?.Report(new RepositoryCreationProgressDto(
                "scan",
                22,
                0,
                0,
                $"Scanning {targetRepositories.Count} repositor{(targetRepositories.Count == 1 ? "y" : "ies")}"));

            for (var index = 0; index < targetRepositories.Count; index++)
            {
                ct.ThrowIfCancellationRequested();

                var repository = targetRepositories[index];
                var basePercent = 22 + (int)Math.Round(index * 68d / totalRepositories);
                var spanPercent = Math.Max(12, (int)Math.Ceiling(68d / totalRepositories));

                var repositoryProgress = request.Progress is null
                    ? null
                    : new Progress<RepositoryScanProgressDto>(progress =>
                    {
                        var normalizedPercent = Math.Clamp(progress.Percent, 0, 100);
                        var mappedPercent = Math.Min(94, basePercent + (int)Math.Round(spanPercent * normalizedPercent / 100d));
                        var currentFiles = scannedFiles + Math.Max(progress.FilesProcessed, progress.FilesTotal);
                        request.Progress.Report(new RepositoryCreationProgressDto(
                            progress.Stage,
                            mappedPercent,
                            currentFiles,
                            currentFiles,
                            $"{repository.Name}: {progress.Message}"));
                    });

                var result = await scanner.ScanRepositoryAsync(
                    repository.Id,
                    repositoryProgress,
                    new RepositoryScanOptionsDto(
                        SaveFileVersions: true,
                        TriggerOverride: "initial_snapshot"),
                    ct);
                scannedFiles += result.FileEntries;

                request.Progress?.Report(new RepositoryCreationProgressDto(
                    "scan",
                    Math.Min(94, basePercent + spanPercent),
                    scannedFiles,
                    scannedFiles,
                    $"{repository.Name}: scan completed"));
            }

            log.LogInformation("Initial native scan completed for selected repositories");

            request.Progress?.Report(new RepositoryCreationProgressDto(
                "finalize",
                96,
                scannedFiles,
                scannedFiles,
                "Applying setup"));

            await native.ApplySetupAsync(ct);

            request.Progress?.Report(new RepositoryCreationProgressDto(
                "done",
                100,
                scannedFiles,
                scannedFiles,
                "Initial sync completed"));

            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Initial setup failed");
            return OperationResult.Fail(ex.Message);
        }
    }
}
