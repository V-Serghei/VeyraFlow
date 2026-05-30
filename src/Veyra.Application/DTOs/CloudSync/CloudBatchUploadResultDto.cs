namespace Veyra.Application.DTOs.CloudSync;

public sealed record CloudBatchUploadResultDto(
    bool Ok,
    int StoredBlocks,
    int SkippedBlocks);
