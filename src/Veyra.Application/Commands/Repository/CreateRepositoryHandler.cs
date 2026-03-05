using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Common.Results;

namespace Veyra.Application.Commands.Repository;

public sealed class CreateRepositoryHandler(
    IRepositoryRepository repo,
    ISetupRepository setup,
    ILogger<CreateRepositoryHandler> log)
    : IRequestHandler<CreateRepositoryCommand, OperationResult<int>>
{
    public async Task<OperationResult<int>> Handle(CreateRepositoryCommand request, CancellationToken ct)
    {
        try
        {
            // Ensure directory is tracked
            await setup.AddWatchedDirectoryAsync(request.DirectoryPath, ct);

            var dirs = await setup.GetWatchedDirectoriesAsync(ct);
            // Find matching dir by path (normalized)
            // We need the directory ID, so we go through the repo
            await repo.EnsureRepositoriesForAllDirectoriesAsync(ct);

            var all = await repo.GetAllRepositoriesAsync(ct);
            var match = all.FirstOrDefault(r =>
                r.DirectoryPath.Equals(request.DirectoryPath, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                // Update name if provided
                if (!string.IsNullOrWhiteSpace(request.Name))
                    await repo.UpdateRepositoryAsync(match.Id, request.Name, request.Description, ct);

                log.LogInformation("Repository ensured for {Path}: Id={Id}", request.DirectoryPath, match.Id);
                return OperationResult<int>.Ok(match.Id);
            }

            return OperationResult<int>.Fail("Failed to create repository for directory.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to create repository for {Path}", request.DirectoryPath);
            return OperationResult<int>.Fail(ex.Message);
        }
    }
}
