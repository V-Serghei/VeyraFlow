using Veyra.Domain.Entities;

namespace Veyra.Infrastructure.Data.Setup.Models.Snapshots;

internal sealed record PendingTextDiffPrecompute(
    string RelativePath,
    long LeftFileVersionId,
    FileVersion RightFileVersion);
