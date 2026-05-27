using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Native.Diffing;

internal sealed record NativeTextDiffLine
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "equal";

    [JsonPropertyName("left_line_number")]
    public int? LeftLineNumber { get; init; }

    [JsonPropertyName("right_line_number")]
    public int? RightLineNumber { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}
