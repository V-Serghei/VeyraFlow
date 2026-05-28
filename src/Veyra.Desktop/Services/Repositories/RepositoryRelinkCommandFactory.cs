using Veyra.Application.Commands.Repository;
using Veyra.Application.DTOs;

namespace Veyra.Desktop.Services.Repositories;

internal static class RepositoryRelinkCommandFactory
{
    public static UpdateRepositoryConfigurationCommand Create(RepositoryDto repository, string newPath)
    {
        return new UpdateRepositoryConfigurationCommand(
            repository.Id,
            repository.Name,
            repository.Description,
            newPath,
            repository.LinkedFormats,
            repository.AutoCaptureFileVersions,
            repository.ProtectCloudMetadata,
            repository.ExcludedPatterns,
            repository.RetentionPolicy,
            repository.CloudSync?.ConflictStrategy ?? RepositorySyncConflictStrategies.LastWriteWins,
            repository.CloudSync?.RetryMaxAttempts ?? 5,
            repository.CloudSync?.RetryBaseDelaySeconds ?? 30);
    }
}
