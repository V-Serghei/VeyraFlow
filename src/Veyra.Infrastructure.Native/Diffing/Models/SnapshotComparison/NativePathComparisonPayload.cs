using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Native.Diffing;

internal sealed record NativePathComparisonPayload
{
    [JsonPropertyName("added_count")]
    public int AddedCount { get; init; }

    [JsonPropertyName("modified_count")]
    public int ModifiedCount { get; init; }

    [JsonPropertyName("deleted_count")]
    public int DeletedCount { get; init; }

    [JsonPropertyName("changed_files_count")]
    public int ChangedFilesCount { get; init; }

    [JsonPropertyName("changes")]
    public List<NativePathChangePayload> Changes { get; init; } = [];
}
