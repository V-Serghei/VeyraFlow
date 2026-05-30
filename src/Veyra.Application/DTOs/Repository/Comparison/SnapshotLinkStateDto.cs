namespace Veyra.Application.DTOs.Repository.Comparison;

public sealed record SnapshotLinkStateDto(
    long FileIdentityId,
    long FileVersionId,
    bool IsDeletionMarker,
    long SizeBytes,
    DateTime VersionCreatedAtUtc,
    string RelativePath,
    string Name);
