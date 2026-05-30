namespace Veyra.Application.DTOs.Repository.Bundles;

public sealed record RepositoryBundleExportResultDto(
    int RepositoryId,
    string BundlePath,
    int SnapshotCount,
    int FileIdentityCount,
    int FileVersionCount,
    int DiffCount,
    int BlockFileCount,
    long BundleSizeBytes,
    DateTime ExportedAtUtc,
    string Summary);
