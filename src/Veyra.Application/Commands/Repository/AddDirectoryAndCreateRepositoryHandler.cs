using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;

namespace Veyra.Application.Commands.Repository;

public sealed class AddDirectoryAndCreateRepositoryHandler(
    ISetupRepository setup,
    IRepositoryRepository repo,
    IRepositoryScanner scanner,
    INativeSetupApplier native,
    ILogger<AddDirectoryAndCreateRepositoryHandler> log)
    : IRequestHandler<AddDirectoryAndCreateRepositoryCommand, OperationResult<int>>
{
    public async Task<OperationResult<int>> Handle(AddDirectoryAndCreateRepositoryCommand request, CancellationToken ct)
    {
        try
        {
            await setup.AddWatchedDirectoryAsync(request.DirectoryPath, ct);
            await repo.EnsureRepositoriesForAllDirectoriesAsync(ct);

            var all = await repo.GetAllRepositoriesAsync(ct);
            var match = all.FirstOrDefault(r =>
                r.DirectoryPath.Equals(request.DirectoryPath, StringComparison.OrdinalIgnoreCase));

            if (match is null)
                return OperationResult<int>.Fail("Directory was added but repository creation failed.");

            if (!string.IsNullOrWhiteSpace(request.RepositoryName))
                await repo.UpdateRepositoryAsync(match.Id, request.RepositoryName, null, ct);

            await scanner.ScanRepositoryAsync(
                match.Id,
                null,
                new RepositoryScanOptionsDto(
                    SaveFileVersions: false,
                    TriggerOverride: "sync_index_setup"),
                ct);

            await native.ApplySetupAsync(ct);

            log.LogInformation("Added directory and created repository for {Path}", request.DirectoryPath);
            return OperationResult<int>.Ok(match.Id);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to add directory and create repository");
            return OperationResult<int>.Fail(ex.Message);
        }
    }
}

