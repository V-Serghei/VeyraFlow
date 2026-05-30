namespace Veyra.Application.DTOs;

public sealed record FileVersionRestoreDto(
    long FileVersionId,
    int RepositoryId,
    string RelativePath,
    string? Extension,
    long SizeBytes,
    bool IsDeletionMarker,
    string ContentHashSha256,
    DateTime LastWriteUtc,
    IReadOnlyList<StoredFileBlockDto> Blocks);
