namespace Veyra.Application.DTOs;

public sealed record CloudStorageFilesystemStatsDto(
    long PackFileCount,
    long LooseFileCount,
    long OtherFileCount,
    long PackFileBytes,
    long LooseFileBytes,
    long OtherFileBytes,
    long TotalPhysicalBytes);
