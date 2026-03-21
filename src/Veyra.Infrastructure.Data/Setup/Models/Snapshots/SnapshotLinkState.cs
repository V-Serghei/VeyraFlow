using System;

namespace Veyra.Infrastructure.Data.Setup.Models.Snapshots;

internal sealed record SnapshotLinkState(
    long SnapshotId,
    long FileIdentityId,
    long FileVersionId,
    bool IsDeletionMarker,
    long SizeBytes,
    DateTime VersionCreatedAtUtc,
    string RelativePath,
    string Name);
