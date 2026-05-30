namespace Veyra.Application.DTOs.PendingChanges;

public sealed record PendingBinaryDiffSummaryDto(
    long BaselineSizeBytes,
    long CurrentSizeBytes,
    long SizeDeltaBytes,
    string BaselineHashSha256,
    string CurrentHashSha256,
    int ChunkSizeBytes,
    int BaselineBlockCount,
    int CurrentBlockCount,
    int SharedBlockCount,
    double? DedupRatio,
    double? ChangedBlockRatio,
    double? ByteSimilarityRatio);
