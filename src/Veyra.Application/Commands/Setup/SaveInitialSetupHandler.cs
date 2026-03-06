using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Common.Results;

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
            await setupRepo.SaveInitialSetupAsync(request.Directories, request.Extensions, ct);
            log.LogInformation(
                "Initial setup saved: {DirCount} dirs, {ExtCount} extensions, all linked",
                request.Directories.Count,
                request.Extensions.Count);

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

            await scanner.ScanAllRepositoriesAsync(ct);
            log.LogInformation("Initial native scan completed for all repositories");

            await native.ApplySetupAsync(ct);

            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Initial setup failed");
            return OperationResult.Fail(ex.Message);
        }
    }
}
