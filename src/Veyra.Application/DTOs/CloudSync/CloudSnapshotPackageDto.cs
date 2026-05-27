using System.Collections.Generic;

namespace Veyra.Application.DTOs;

public sealed record CloudSnapshotPackageDto(
    CloudRepositoryMetadataDto Repository,
    CloudSnapshotMetadataDto Snapshot,
    IReadOnlyList<CloudSnapshotEntryDto> Entries,
    IReadOnlyList<CloudFileVersionDto> FileVersions,
    bool MetadataProtected = false);
