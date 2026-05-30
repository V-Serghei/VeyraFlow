using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Common.Results;

namespace Veyra.Application.Commands.Repository;

public sealed class RestoreFileVersionHandler(
    IRepositoryRepository repositories,
    IRepositorySnapshotRepository snapshots,
    IFileContentStore contentStore,
    ILogger<RestoreFileVersionHandler> log)
    : IRequestHandler<RestoreFileVersionCommand, OperationResult<string>>
{
    public async Task<OperationResult<string>> Handle(RestoreFileVersionCommand request, CancellationToken ct)
    {
        try
        {
            var repo = await repositories.GetRepositoryByIdAsync(request.RepositoryId, ct);
            if (repo is null || repo.IsDeleted)
                return OperationResult<string>.Fail("Repository was not found.");

            var restoreData = await snapshots.GetFileVersionRestoreDataAsync(request.FileVersionId, ct);
            if (restoreData is null)
                return OperationResult<string>.Fail("File version was not found.");

            if (restoreData.RepositoryId != request.RepositoryId)
                return OperationResult<string>.Fail("File version does not belong to the selected repository.");

            var expectedPath = NormalizeRelativePath(request.RelativePath);
            if (!restoreData.RelativePath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                return OperationResult<string>.Fail("File version does not match the requested path.");

            if (restoreData.IsDeletionMarker)
                return OperationResult<string>.Fail("This version is a deletion marker and does not contain restore data.");

            if (restoreData.Blocks.Count == 0 && restoreData.SizeBytes > 0)
                return OperationResult<string>.Fail("Content blocks are missing for this version.");

            var targetPath = ResolveTargetPath(repo.DirectoryPath, restoreData.RelativePath, request.OverwriteCurrent, request.TargetPath);
            var overwrite = request.OverwriteCurrent;

            await contentStore.RestoreFileAsync(restoreData.Blocks, targetPath, overwrite, restoreData.ContentHashSha256, ct);
            TrySetLastWriteTimeUtc(targetPath, restoreData.LastWriteUtc);

            log.LogInformation(
                "File version restored. RepositoryId {RepositoryId}. VersionId {VersionId}. Target {Target}. Overwrite {Overwrite}",
                request.RepositoryId,
                request.FileVersionId,
                targetPath,
                overwrite);

            return OperationResult<string>.Ok(targetPath);
        }
        catch (Exception ex)
        {
            log.LogError(ex,
                "Failed to restore file version. RepositoryId {RepositoryId}. VersionId {VersionId}",
                request.RepositoryId,
                request.FileVersionId);

            return OperationResult<string>.Fail(ex.Message);
        }
    }

    private static string ResolveTargetPath(
        string repositoryRoot,
        string relativePath,
        bool overwriteCurrent,
        string? requestedTargetPath)
    {
        var currentPath = ToAbsolutePath(repositoryRoot, relativePath);

        if (overwriteCurrent)
            return currentPath;

        if (!string.IsNullOrWhiteSpace(requestedTargetPath))
            return Path.GetFullPath(requestedTargetPath.Trim());

        return BuildDefaultRestoredPath(currentPath);
    }

    private static string BuildDefaultRestoredPath(string currentPath)
    {
        var dir = Path.GetDirectoryName(currentPath) ?? Directory.GetCurrentDirectory();
        var fileName = Path.GetFileNameWithoutExtension(currentPath);
        var ext = Path.GetExtension(currentPath);
        var suffix = DateTime.UtcNow.ToString("yyyyMMddHHmmss");

        return Path.Combine(dir, $"{fileName}.restored.{suffix}{ext}");
    }

    private static string NormalizeRelativePath(string value)
        => value.Trim().Replace('\\', '/');

    private static void TrySetLastWriteTimeUtc(string path, DateTime lastWriteUtc)
    {
        if (lastWriteUtc == default || !File.Exists(path))
            return;

        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.SpecifyKind(lastWriteUtc, DateTimeKind.Utc));
        }
        catch
        {
            // Restoring bytes is the important part; timestamp restore is best effort.
        }
    }

    private static string ToAbsolutePath(string rootPath, string relativePath)
    {
        var rel = NormalizeRelativePath(relativePath).Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(rootPath, rel);
    }
}
