namespace Veyra.Application.DTOs.ArtifactKeys;

public sealed record ArtifactKeyRecordDto(
    string KeyId,
    string Status,
    DateTime CreatedAtUtc,
    DateTime? RotatedAtUtc,
    DateTime? RevokedAtUtc,
    string? Note);
