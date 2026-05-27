using Veyra.Domain.Entities;

namespace Veyra.Infrastructure.Data.Setup.Models.Snapshots;

internal sealed record PendingSnapshotFileLink(long FileIdentityId, FileVersion FileVersion);
