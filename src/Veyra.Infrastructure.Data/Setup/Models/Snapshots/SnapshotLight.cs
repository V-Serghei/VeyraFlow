using System;

namespace Veyra.Infrastructure.Data.Setup.Models.Snapshots;

internal sealed record SnapshotLight(
    long SnapshotId,
    string? Title,
    DateTime CreatedAtUtc,
    bool IsArchived,
    string Trigger,
    string? TagsCsv);
