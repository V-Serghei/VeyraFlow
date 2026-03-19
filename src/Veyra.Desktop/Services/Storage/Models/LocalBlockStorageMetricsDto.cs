namespace Veyra.Desktop.Services.Storage.Models;

public sealed record LocalBlockStorageMetricsDto(
    int RepositoryCount,
    long ReferencedBlockCount,
    long UniqueBlockCount,
    long MissingBlockCount,
    long LogicalReferencedBytes,
    long PhysicalStoredBytes,
    long SavedBytes,
    long ReducedPercentFloor,
    LocalBlockStorageFilesystemMetricsDto Filesystem);
