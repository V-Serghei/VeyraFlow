using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Native.Diffing;

internal sealed record NativeTextDiffHunk
{
    [JsonPropertyName("sequence")]
    public int Sequence { get; init; }

    [JsonPropertyName("start_line_sequence")]
    public int StartLineSequence { get; init; }

    [JsonPropertyName("end_line_sequence")]
    public int EndLineSequence { get; init; }

    [JsonPropertyName("old_start_line")]
    public int OldStartLine { get; init; }

    [JsonPropertyName("old_line_count")]
    public int OldLineCount { get; init; }

    [JsonPropertyName("new_start_line")]
    public int NewStartLine { get; init; }

    [JsonPropertyName("new_line_count")]
    public int NewLineCount { get; init; }

    [JsonPropertyName("change_kind")]
    public string? ChangeKind { get; init; }
}
