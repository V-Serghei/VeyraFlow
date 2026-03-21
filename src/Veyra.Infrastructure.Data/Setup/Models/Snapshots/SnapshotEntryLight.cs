using System;

namespace Veyra.Infrastructure.Data.Setup.Models.Snapshots;

internal sealed record SnapshotEntryLight(
    string RelativePath,
    string Name,
    long SizeBytes,
    DateTime LastWriteUtc,
    string? ContentHashSha256);
