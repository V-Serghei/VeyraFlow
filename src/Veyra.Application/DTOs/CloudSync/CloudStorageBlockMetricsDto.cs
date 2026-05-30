namespace Veyra.Application.DTOs.CloudSync;

public sealed record CloudStorageBlockMetricsDto(
    long TotalBlocks,
    long PackedBlocks,
    long LooseBlocks,
    long MissingBlocks,
    long LogicalBytes,
    long PackedBytes,
    long LooseBytes,
    long MissingBytes);
