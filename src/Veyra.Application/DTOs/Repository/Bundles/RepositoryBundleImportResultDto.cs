namespace Veyra.Application.DTOs;

public sealed record RepositoryBundleImportResultDto(
    int RepositoryId,
    string RepositoryName,
    string TargetDirectoryPath,
    int SnapshotCount,
    int FileIdentityCount,
    int FileVersionCount,
    int DiffCount,
    int BlockFileCount,
    DateTime ImportedAtUtc,
    IReadOnlyList<string> Warnings,
    string Summary);
