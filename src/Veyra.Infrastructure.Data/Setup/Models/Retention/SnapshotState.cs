using System;

namespace Veyra.Infrastructure.Data.Setup.Models.Retention;

internal sealed record SnapshotState(
    long Id,
    DateTime CreatedAt,
    long TotalFileBytes,
    string Trigger);
