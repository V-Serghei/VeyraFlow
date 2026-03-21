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
                return OperationResult<string>.Fail("Репозиторий не найден.");

            var restoreData = await snapshots.GetFileVersionRestoreDataAsync(request.FileVersionId, ct);
            if (restoreData is null)
                return OperationResult<string>.Fail("Версия файла не найдена.");

            if (restoreData.RepositoryId != request.RepositoryId)
                return OperationResult<string>.Fail("Версия файла не принадлежит репозиторию.");

            var expectedPath = NormalizeRelativePath(request.RelativePath);
            if (!restoreData.RelativePath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                return OperationResult<string>.Fail("Версия файла не соответствует указанному пути.");

            if (restoreData.IsDeletionMarker)
                return OperationResult<string>.Fail("Эта версия помечена как удаление и не содержит данных для восстановления.");

            if (restoreData.Blocks.Count == 0 && restoreData.SizeBytes > 0)
                return OperationResult<string>.Fail("Для версии отсутствуют блоки содержимого.");

            var targetPath = ResolveTargetPath(repo.DirectoryPath, restoreData.RelativePath, request.OverwriteCurrent, request.TargetPath);
            var overwrite = request.OverwriteCurrent;

            await contentStore.RestoreFileAsync(restoreData.Blocks, targetPath, overwrite, ct);

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

    private static string ToAbsolutePath(string rootPath, string relativePath)
    {
        var rel = NormalizeRelativePath(relativePath).Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(rootPath, rel);
    }
}
