using System;

namespace Veyra.Infrastructure.Data.Setup.Models.Recovery;

internal sealed record SnapshotState(long SnapshotId, DateTime CreatedAtUtc);
