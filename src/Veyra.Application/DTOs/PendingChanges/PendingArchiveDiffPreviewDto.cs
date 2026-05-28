namespace Veyra.Application.DTOs;

public sealed record PendingArchiveDiffPreviewDto
{
    public string ArchiveFormat { get; init; } = "zip";
    public int BaselineEntryCount { get; init; }
    public int CurrentEntryCount { get; init; }
    public int AddedEntryCount { get; init; }
    public int RemovedEntryCount { get; init; }
    public int ChangedEntryCount { get; init; }
    public int UnchangedEntryCount { get; init; }
    public IReadOnlyList<PendingArchiveEntryDiffDto> Entries { get; init; } = Array.Empty<PendingArchiveEntryDiffDto>();
}
