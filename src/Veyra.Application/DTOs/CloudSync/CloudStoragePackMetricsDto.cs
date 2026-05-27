namespace Veyra.Application.DTOs;

public sealed record CloudStoragePackMetricsDto(
    long TotalPacks,
    long ActivePacks,
    long SealedPacks,
    long BytesWritten,
    long PackedBlockRefs);
