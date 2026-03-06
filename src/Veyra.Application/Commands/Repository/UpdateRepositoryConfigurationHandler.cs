using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Common.Results;

namespace Veyra.Application.Commands.Repository;

public sealed class UpdateRepositoryConfigurationHandler(
    IRepositoryRepository repositories,
    ISetupRepository setup,
    IRepositoryScanner scanner,
    INativeSetupApplier native,
    ILogger<UpdateRepositoryConfigurationHandler> log)
    : IRequestHandler<UpdateRepositoryConfigurationCommand, OperationResult>
{
    public async Task<OperationResult> Handle(UpdateRepositoryConfigurationCommand request, CancellationToken ct)
    {
        try
        {
            var repo = await repositories.GetRepositoryByIdAsync(request.RepositoryId, ct);
            if (repo is null || repo.IsDeleted)
                return OperationResult.Fail("Репозиторий не найден.");

            var normalizedPath = NormalizeDirectoryPath(request.DirectoryPath);
            if (string.IsNullOrWhiteSpace(normalizedPath) || !Directory.Exists(normalizedPath))
                return OperationResult.Fail("Невалидная директория репозитория.");

            var normalizedFormats = NormalizeFormats(request.Formats);
            if (normalizedFormats.Count == 0)
                return OperationResult.Fail("Выберите хотя бы один формат.");

            var safeName = string.IsNullOrWhiteSpace(request.Name)
                ? repo.Name
                : request.Name.Trim();

            if (!PathEquals(repo.DirectoryPath, normalizedPath))
                await setup.UpdateWatchedDirectoryAsync(repo.DirectoryPath, normalizedPath, ct);

            await repositories.UpdateRepositoryAsync(repo.Id, safeName, request.Description, ct);

            var globalFormats = await setup.GetTrackedExtensionsAsync(ct);
            var missingGlobal = normalizedFormats
                .Where(f => !globalFormats.Contains(f, StringComparer.OrdinalIgnoreCase))
                .ToList();

            foreach (var ext in missingGlobal)
                await setup.AddTrackedExtensionAsync(ext, ct);

            var linkedFormats = await setup.GetFormatsForDirectoryAsync(normalizedPath, ct);

            var toLink = normalizedFormats
                .Where(f => !linkedFormats.Contains(f, StringComparer.OrdinalIgnoreCase))
                .ToList();

            var toUnlink = linkedFormats
                .Where(f => !normalizedFormats.Contains(f, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (toUnlink.Count > 0)
                await setup.UnlinkDirectoryFromFormatsAsync(normalizedPath, toUnlink, ct);

            if (toLink.Count > 0)
                await setup.LinkDirectoryToFormatsAsync(normalizedPath, toLink, ct);

            await scanner.ScanRepositoryAsync(repo.Id, null, ct);
            await native.ApplySetupAsync(ct);

            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to update repository configuration {RepositoryId}", request.RepositoryId);
            return OperationResult.Fail(ex.Message);
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
