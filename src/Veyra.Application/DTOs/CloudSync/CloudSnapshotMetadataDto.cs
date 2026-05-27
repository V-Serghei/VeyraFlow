namespace Veyra.Application.DTOs;

public sealed record CloudSnapshotMetadataDto(
    long Id,
    string? Title,
    string Trigger,
    DateTime CreatedAtUtc,
    int TotalEntries,
    int FileEntries,
    int DirectoryEntries,
    long TotalFileBytes,
    string? PayloadSha256);
