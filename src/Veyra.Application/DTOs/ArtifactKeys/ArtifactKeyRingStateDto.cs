namespace Veyra.Application.DTOs.ArtifactKeys;

public sealed record ArtifactKeyRingStateDto(
    string ActiveKeyId,
    DateTime UpdatedAtUtc,
    IReadOnlyList<ArtifactKeyRecordDto> Keys);
