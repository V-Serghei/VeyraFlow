using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.DTOs;

namespace Veyra.Application.Commands.Repository;

public sealed class UnlinkFormatsFromRepositoryHandler(
    IRepositoryRepository repoRepo,
    ISetupRepository setup,
    IRepositoryScanner scanner,
    ILogger<UnlinkFormatsFromRepositoryHandler> log)
    : IRequestHandler<UnlinkFormatsFromRepositoryCommand>
{
    public async Task Handle(UnlinkFormatsFromRepositoryCommand request, CancellationToken ct)
    {
        var repo = await repoRepo.GetRepositoryByIdAsync(request.RepositoryId, ct);
        if (repo is null) return;

        await setup.UnlinkDirectoryFromFormatsAsync(repo.DirectoryPath, request.FormatPatterns, ct);

        await scanner.ScanRepositoryAsync(
            request.RepositoryId,
            null,
            new RepositoryScanOptionsDto(
                SaveFileVersions: false,
                TriggerOverride: "sync_index_format_update"),
            ct);

        log.LogInformation("Unlinked {Count} formats from repository {Id}", request.FormatPatterns.Count, request.RepositoryId);
    }
}

