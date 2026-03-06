using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;

namespace Veyra.Application.Commands.Repository;

public sealed class CreateRepositoryWithFormatsHandler(
    ISetupRepository setup,
    IRepositoryRepository repositories,
    IRepositoryScanner scanner,
    INativeSetupApplier native,
    ILogger<CreateRepositoryWithFormatsHandler> log)
    : IRequestHandler<CreateRepositoryWithFormatsCommand, OperationResult<int>>
{
    public async Task<OperationResult<int>> Handle(CreateRepositoryWithFormatsCommand request, CancellationToken ct)
    {
        try
        {
            var path = NormalizeDirectoryPath(request.DirectoryPath);
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return OperationResult<int>.Fail("Невалидная директория репозитория.");

            var name = string.IsNullOrWhiteSpace(request.Name)
                ? Path.GetFileName(path.TrimEnd('\\', '/'))
                : request.Name.Trim();

            var formats = NormalizeFormats(request.Formats);
            if (formats.Count == 0)
                return OperationResult<int>.Fail("Выберите хотя бы один формат.");

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
                "Подготовка репозитория"));

            await setup.AddWatchedDirectoryAsync(path, ct);
            await repositories.EnsureRepositoriesForAllDirectoriesAsync(ct);

            var allRepos = await repositories.GetAllRepositoriesAsync(ct);
            var repo = allRepos.FirstOrDefault(r => PathEquals(r.DirectoryPath, path));

            if (repo is null)
                return OperationResult<int>.Fail("Не удалось создать репозиторий для директории.");

            await repositories.UpdateRepositoryAsync(repo.Id, name, request.Description, ct);

            request.Progress?.Report(new RepositoryCreationProgressDto(
                "formats",
                20,
                0,
                0,
                "Применение форматов"));

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
                40,
                0,
                0,
                "Сканирование файлов"));

            var scanProgress = new Progress<RepositoryScanProgressDto>(p =>
            {
                request.Progress?.Report(new RepositoryCreationProgressDto(
                    p.Stage,
                    Math.Max(40, p.Percent),
                    p.FilesProcessed,
                    p.FilesTotal,
                    p.Message));
            });

            await scanner.ScanRepositoryAsync(repo.Id, scanProgress, null, ct);

            request.Progress?.Report(new RepositoryCreationProgressDto(
                "sync",
                95,
                0,
                0,
                "Синхронизация локальной конфигурации"));

            await native.ApplySetupAsync(ct);

            request.Progress?.Report(new RepositoryCreationProgressDto(
                "done",
                100,
                0,
                0,
                "Репозиторий создан"));

            log.LogInformation("Repository created successfully. RepositoryId {RepositoryId}", repo.Id);
            return OperationResult<int>.Ok(repo.Id);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to create repository with formats for {Path}", request.DirectoryPath);
            return OperationResult<int>.Fail(ex.Message);
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

