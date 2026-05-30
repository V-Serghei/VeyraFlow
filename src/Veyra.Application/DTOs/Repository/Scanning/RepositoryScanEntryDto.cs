namespace Veyra.Application.DTOs.Repository.Scanning;

public sealed record RepositoryScanEntryDto(
    string RelativePath,
    string? ParentRelativePath,
    string Name,
    bool IsDirectory,
    string? Extension,
    long SizeBytes,
    DateTime LastWriteUtc,
    string? ContentHashSha256,
    DateTime? IndexedAtUtc = null);
