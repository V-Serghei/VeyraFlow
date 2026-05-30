using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Core;
using Veyra.Application.DTOs.Repository.Scanning;

namespace Veyra.Application.Commands.Repository;

public sealed class CreateRepositoryWithFormatsHandler(
    ISetupRepository setup,
    IRepositoryRepository repositories,
    IRepositoryScanner scanner,
    INativeSetupApplier native,
    ILogger<CreateRepositoryWithFormatsHandler> log)
    : IRequestHandler<CreateRepositoryWithFormatsCommand, OperationResult<RepositoryCreationOutcomeDto>>
{
    public async Task<OperationResult<RepositoryCreationOutcomeDto>> Handle(CreateRepositoryWithFormatsCommand request, CancellationToken ct)
    {
        try
        {
            var path = NormalizeDirectoryPath(request.DirectoryPath);
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return OperationResult<RepositoryCreationOutcomeDto>.Fail("Specified directory does not exist.");

            var name = string.IsNullOrWhiteSpace(request.Name)
                ? Path.GetFileName(path.TrimEnd('\\', '/'))
                : request.Name.Trim();

            var formats = NormalizeFormats(request.Formats);
            if (formats.Count == 0)
                return OperationResult<RepositoryCreationOutcomeDto>.Fail("No tracking formats were selected.");

            log.LogInformation(
                "Creating repository. Name {Name}. Path {Path}. Formats {FormatCount}",
                name,
                path,
                formats.Count);

            request.Progress?.Report(new RepositoryCreationProgressDto(
                "prepare",
                5,
                0,
                0,
                "Preparing repository"));

            await setup.AddWatchedDirectoryAsync(path, ct);
            await repositories.EnsureRepositoriesForAllDirectoriesAsync(ct);

            var allRepos = await repositories.GetAllRepositoriesAsync(ct);
            var repo = allRepos.FirstOrDefault(r => PathEquals(r.DirectoryPath, path));

            if (repo is null)
                return OperationResult<RepositoryCreationOutcomeDto>.Fail("Failed to create repository for the selected directory.");

            await repositories.UpdateRepositoryAsync(repo.Id, name, request.Description, ct);

            request.Progress?.Report(new RepositoryCreationProgressDto(
                "formats",
                10,
                0,
                0,
                "Applying tracking formats"));

            var globalFormats = await setup.GetTrackedExtensionsAsync(ct);
            var missingGlobal = formats
                .Where(f => !globalFormats.Contains(f, StringComparer.OrdinalIgnoreCase))
                .ToList();

            foreach (var ext in missingGlobal)
                await setup.AddTrackedExtensionAsync(ext, ct);

            var linkedFormats = await setup.GetFormatsForDirectoryAsync(path, ct);

            var toLink = formats
                .Where(f => !linkedFormats.Contains(f, StringComparer.OrdinalIgnoreCase))
                .ToList();

            var toUnlink = linkedFormats
                .Where(f => !formats.Contains(f, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (toUnlink.Count > 0)
                await setup.UnlinkDirectoryFromFormatsAsync(path, toUnlink, ct);

            if (toLink.Count > 0)
                await setup.LinkDirectoryToFormatsAsync(path, toLink, ct);

            request.Progress?.Report(new RepositoryCreationProgressDto(
                "scan",
                12,
                0,
                0,
                "Scanning directory"));

            var reportedPercent = 12;
            var lastFilesProcessed = 0;
            var lastFilesTotal = 0;

            var scanProgress = new Progress<RepositoryScanProgressDto>(p =>
            {
                var mapped = Math.Clamp(p.Percent, 0, 100);
                if (mapped < reportedPercent)
                    mapped = reportedPercent;

                reportedPercent = mapped;
                lastFilesProcessed = Math.Max(lastFilesProcessed, p.FilesProcessed);
                lastFilesTotal = Math.Max(lastFilesTotal, p.FilesTotal);

                request.Progress?.Report(new RepositoryCreationProgressDto(
                    p.Stage,
                    mapped,
                    lastFilesProcessed,
                    lastFilesTotal,
                    p.Message));
            });

            var scanResult = await InitialSnapshotCreation.RunWithBoundedRetryAsync(
                scanner,
                repo.Id,
                scanProgress,
                log,
                ct);

            request.Progress?.Report(new RepositoryCreationProgressDto(
                "sync",
                Math.Clamp(reportedPercent, 1, 99),
                lastFilesProcessed,
                lastFilesTotal,
                "Applying system configuration"));

            await native.ApplySetupAsync(ct);

            request.Progress?.Report(new RepositoryCreationProgressDto(
                "done",
                100,
                lastFilesProcessed,
                lastFilesTotal,
                "Repository created"));

            var snapshotStatus = InitialSnapshotCreation.ResolveSnapshotStatus(scanResult);
            if (InitialSnapshotCreation.RequiresUserRetry(scanResult))
            {
                log.LogWarning(
                    "Repository created but initial snapshot requires retry. RepositoryId {RepositoryId}. Path {Path}. Files {Files}. NoChanges {NoChanges}. ScanInProgress {ScanInProgress}. BusyFiles {BusyFiles}",
                    repo.Id,
                    path,
                    scanResult.FileEntries,
                    scanResult.NoChangesDetected,
                    scanResult.SkippedBecauseScanInProgress,
                    scanResult.BusyFilesCount);
            }
            else if (scanResult.HasBusyFiles)
            {
                log.LogWarning(
                    "Repository created with busy-file warnings. RepositoryId {RepositoryId}. BusyFiles {BusyFiles}",
                    repo.Id,
                    scanResult.BusyFilesCount);
            }
            else
            {
                log.LogInformation("Repository created successfully. RepositoryId {RepositoryId}", repo.Id);
            }

            var summary = $"Repository created. Directory scan matched {scanResult.FileEntries} file(s) and {scanResult.DirectoryEntries} folder(s) using {formats.Count} selected format(s). Initial versioned snapshot {snapshotStatus}. Busy files: {scanResult.BusyFilesCount}.";

            return OperationResult<RepositoryCreationOutcomeDto>.Ok(
                new RepositoryCreationOutcomeDto(
                    repo.Id,
                    scanResult.BusyFilesSafe),
                summary);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to create repository with formats for {Path}", request.DirectoryPath);
            return OperationResult<RepositoryCreationOutcomeDto>.Fail(ex.Message);
        }
    }

    private static string NormalizeDirectoryPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        try
        {
            return Path.GetFullPath(value.Trim().Replace('/', '\\')).TrimEnd('\\', '/');
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool PathEquals(string left, string right)
        => left.TrimEnd('\\', '/').Equals(right.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static List<string> NormalizeFormats(IEnumerable<string> values)
    {
        return values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Select(v => v.StartsWith('.') ? v : "." + v)
            .Select(v => v.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
