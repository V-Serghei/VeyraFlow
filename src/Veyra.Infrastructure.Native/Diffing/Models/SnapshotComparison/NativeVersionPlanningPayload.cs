using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Native.Diffing;

internal sealed record NativeVersionPlanningPayload
{
    [JsonPropertyName("changed_files_count")]
    public int ChangedFilesCount { get; init; }

    [JsonPropertyName("new_versions_count")]
    public int NewVersionsCount { get; init; }

    [JsonPropertyName("entries")]
    public List<NativeVersionPlanEntryPayload> Entries { get; init; } = [];
}
