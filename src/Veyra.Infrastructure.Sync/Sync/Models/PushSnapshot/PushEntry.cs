using System;

namespace Veyra.Infrastructure.Sync.Sync;

internal sealed class PushEntry
{
    public string? RelativePath { get; init; }
    public string? ParentRelativePath { get; init; }
    public string? Name { get; init; }
    public bool IsDirectory { get; init; }
    public string? Extension { get; init; }
    public long SizeBytes { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public string? ContentHashSha256 { get; init; }
}
