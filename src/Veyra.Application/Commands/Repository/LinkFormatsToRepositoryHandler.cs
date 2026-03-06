using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;

namespace Veyra.Application.Commands.Repository;

public sealed class LinkFormatsToRepositoryHandler(
    IRepositoryRepository repoRepo,
    ISetupRepository setup,
    IRepositoryScanner scanner,
    ILogger<LinkFormatsToRepositoryHandler> log)
    : IRequestHandler<LinkFormatsToRepositoryCommand>
{
    public async Task Handle(LinkFormatsToRepositoryCommand request, CancellationToken ct)
    {
        var repo = await repoRepo.GetRepositoryByIdAsync(request.RepositoryId, ct);
        if (repo is null) return;

        await setup.LinkDirectoryToFormatsAsync(repo.DirectoryPath, request.FormatPatterns, ct);
        await scanner.ScanRepositoryAsync(request.RepositoryId, null, ct);

        log.LogInformation("Linked {Count} formats to repository {Id}", request.FormatPatterns.Count, request.RepositoryId);
    }
}

