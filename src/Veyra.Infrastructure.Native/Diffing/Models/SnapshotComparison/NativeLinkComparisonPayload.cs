using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Native.Diffing;

internal sealed record NativeLinkComparisonPayload
{
    [JsonPropertyName("changed_files_count")]
    public int ChangedFilesCount { get; init; }

    [JsonPropertyName("changes")]
    public List<NativeLinkChangePayload> Changes { get; init; } = [];
}
