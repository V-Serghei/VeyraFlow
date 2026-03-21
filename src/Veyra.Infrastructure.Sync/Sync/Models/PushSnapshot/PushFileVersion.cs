using System;
using System.Collections.Generic;

namespace Veyra.Infrastructure.Sync.Sync;

internal sealed class PushFileVersion
{
    public string? RelativePath { get; init; }
    public long FileVersionId { get; init; }
    public string? ContentHashSha256 { get; init; }
    public long SizeBytes { get; init; }
    public bool IsDeletionMarker { get; init; }
    public DateTime CreatedAt { get; init; }
    public List<PushBlock>? Blocks { get; init; }
}
