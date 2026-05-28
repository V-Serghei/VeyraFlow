namespace Veyra.Application.DTOs;

public sealed record RepositoryDto(
    int Id,
    string Name,
    string? Description,
    int DirectoryId,
    string DirectoryPath,
    IReadOnlyList<string> LinkedFormats,
    IReadOnlyList<string> ExcludedPatterns,
    bool IsDeleted,
    int FileCount,
    int VersionCount,
    long TotalSizeBytes,
    DateTime? LastScannedAt,
    RepositoryRetentionPolicyDto RetentionPolicy,
    RepositoryCloudSyncStatusDto? CloudSync = null,
    bool AutoCaptureFileVersions = false,
    bool ProtectCloudMetadata = true,
    int ChangedVersionCount = 0);
