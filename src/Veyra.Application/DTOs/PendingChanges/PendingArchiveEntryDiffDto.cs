namespace Veyra.Application.DTOs;

public sealed record PendingArchiveEntryDiffDto
{
    public string EntryPath { get; init; } = string.Empty;
    public PendingArchiveEntryChangeKind ChangeKind { get; init; } = PendingArchiveEntryChangeKind.Unchanged;
    public bool IsDirectory { get; init; }

    public long? BaselineSizeBytes { get; init; }
    public long? CurrentSizeBytes { get; init; }

    public long? BaselineCompressedSizeBytes { get; init; }
    public long? CurrentCompressedSizeBytes { get; init; }

    public uint? BaselineCrc32 { get; init; }
    public uint? CurrentCrc32 { get; init; }

    public DateTimeOffset? BaselineModifiedUtc { get; init; }
    public DateTimeOffset? CurrentModifiedUtc { get; init; }
}
