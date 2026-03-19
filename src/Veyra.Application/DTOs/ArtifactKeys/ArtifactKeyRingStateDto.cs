namespace Veyra.Application.DTOs;

public sealed record ArtifactKeyRingStateDto(
    string ActiveKeyId,
    DateTime UpdatedAtUtc,
    IReadOnlyList<ArtifactKeyRecordDto> Keys);
