namespace Veyra.Application.DTOs.CloudSync;

public sealed record CloudSnapshotPackageDto(
    CloudRepositoryMetadataDto Repository,
    CloudSnapshotMetadataDto Snapshot,
    IReadOnlyList<CloudSnapshotEntryDto> Entries,
    IReadOnlyList<CloudFileVersionDto> FileVersions,
    bool MetadataProtected = false);
