using System;

namespace Veyra.Infrastructure.Data.Setup.Models.Recovery;

internal sealed record VersionState(
    long FileVersionId,
    long FileIdentityId,
    string ContentHashSha256,
    long SizeBytes,
    DateTime CreatedAtUtc,
    bool IsDeletionMarker);
