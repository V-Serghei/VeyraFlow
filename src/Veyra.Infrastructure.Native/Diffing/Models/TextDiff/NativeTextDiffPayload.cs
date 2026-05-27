using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Native.Diffing;

internal sealed record NativeTextDiffPayload
{
    [JsonPropertyName("added_lines")]
    public int AddedLines { get; init; }

    [JsonPropertyName("removed_lines")]
    public int RemovedLines { get; init; }

    [JsonPropertyName("is_truncated")]
    public bool IsTruncated { get; init; }

    [JsonPropertyName("lines")]
    public List<NativeTextDiffLine> Lines { get; init; } = [];

    [JsonPropertyName("hunks")]
    public List<NativeTextDiffHunk> Hunks { get; init; } = [];
}
