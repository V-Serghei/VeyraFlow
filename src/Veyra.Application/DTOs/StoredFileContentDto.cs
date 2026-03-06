namespace Veyra.Application.DTOs;

public sealed record StoredFileContentDto(
    long FileSizeBytes,
    long StoredSizeBytes,
    int BlockCount,
    int DedupedBlocks,
    int NewBlocks,
    IReadOnlyList<StoredFileBlockDto> Blocks);
