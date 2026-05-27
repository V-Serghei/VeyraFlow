namespace Veyra.Application.DTOs;

public sealed record CloudBatchUploadResultDto(
    bool Ok,
    int StoredBlocks,
    int SkippedBlocks);
