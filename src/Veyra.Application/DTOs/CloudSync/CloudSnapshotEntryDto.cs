namespace Veyra.Application.DTOs;

public sealed record CloudSnapshotEntryDto(
    string RelativePath,
    string? ParentRelativePath,
    string Name,
    bool IsDirectory,
    string? Extension,
    long SizeBytes,
    DateTime LastWriteUtc,
    string? ContentHashSha256);
