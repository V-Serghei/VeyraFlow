using MediatR;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Core;
using Veyra.Application.DTOs.Repository.Retention;

namespace Veyra.Application.Commands.Repository;

public sealed record UpdateRepositoryConfigurationCommand(
    int RepositoryId,
    string Name,
    string? Description,
    string DirectoryPath,
    IReadOnlyCollection<string> Formats,
    bool AutoCaptureFileVersions,
    bool ProtectCloudMetadata,
    IReadOnlyCollection<string> ExcludedPatterns,
    RepositoryRetentionPolicyDto RetentionPolicy,
    string SyncConflictStrategy = RepositorySyncConflictStrategies.LastWriteWins,
    int SyncRetryMaxAttempts = 5,
    int SyncRetryBaseDelaySeconds = 30)
    : IRequest<OperationResult>;
