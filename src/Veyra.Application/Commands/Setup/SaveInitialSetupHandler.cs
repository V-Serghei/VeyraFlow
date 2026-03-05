using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Common.Results;

namespace Veyra.Application.Commands.Setup;

public sealed class SaveInitialSetupHandler(
    ISetupRepository setupRepo,
    IRepositoryRepository repoRepo,
    INativeSetupApplier native,
    ILogger<SaveInitialSetupHandler> log)
    : IRequestHandler<SaveInitialSetupCommand, OperationResult>
{
    public async Task<OperationResult> Handle(SaveInitialSetupCommand request, CancellationToken ct)
    {
        try
        {
            // 1. Save directories + formats + link ALL dirs ↔ ALL formats
            await setupRepo.SaveInitialSetupAsync(request.Directories, request.Extensions, ct);
            log.LogInformation(
                "Initial setup saved: {DirCount} dirs, {ExtCount} extensions, all linked",
                request.Directories.Count, request.Extensions.Count);

            // 2. Create repositories for every directory
            await repoRepo.EnsureRepositoriesForAllDirectoriesAsync(ct);
            log.LogInformation("Repositories ensured for all directories");

            // 3. If user provided a custom name and there's exactly one dir, rename the repo
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
            // If multiple dirs, name each repo by folder name (default behavior in EnsureRepositories)
            // User can rename later from the dashboard

            // 4. Apply native setup (watchers etc)
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
