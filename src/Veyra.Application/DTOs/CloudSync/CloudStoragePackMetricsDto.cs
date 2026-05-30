namespace Veyra.Application.DTOs.CloudSync;

public sealed record CloudStoragePackMetricsDto(
    long TotalPacks,
    long ActivePacks,
    long SealedPacks,
    long BytesWritten,
    long PackedBlockRefs);
