using System;
using System.Linq;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;

namespace Veyra.Application.Commands.Setup;

public sealed class SyncWatchedSetupHandler(
    ISetupRepository repo,
    INativeSetupApplier native,
    ILogger<SyncWatchedSetupHandler> log)
    : IRequestHandler<SyncWatchedSetupCommand>
{
    public async Task Handle(SyncWatchedSetupCommand request, CancellationToken cancellationToken)
    {
        var directories = request.Directories
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var extensions = request.Extensions
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await repo.ReplaceWatchedDirectoriesAsync(directories, cancellationToken);
        await repo.ReplaceTrackedExtensionsAsync(extensions, cancellationToken);

        foreach (var directory in directories)
        {
            await repo.LinkDirectoryToFormatsAsync(directory, extensions, cancellationToken);
        }

        var allDirectories = await repo.GetWatchedDirectoriesInfoAsync(cancellationToken);
        foreach (var directory in allDirectories.Where(x => x.IsEnabled))
        {
            var toUnlink = directory.Formats.Except(extensions, StringComparer.OrdinalIgnoreCase).ToList();
            if (toUnlink.Count == 0) continue;

            await repo.UnlinkDirectoryFromFormatsAsync(directory.Path, toUnlink, cancellationToken);
        }

        log.LogInformation("Setup synchronized. Directories: {DirsCount}, Formats: {FormatsCount}", directories.Count, extensions.Count);
        await native.ApplySetupAsync(cancellationToken);
    }
}
