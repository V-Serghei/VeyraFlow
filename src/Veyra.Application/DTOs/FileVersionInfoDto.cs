namespace Veyra.Application.DTOs;

public sealed record FileVersionInfoDto(
    long FileVersionId,
    string RelativePath,
    string? Extension,
    DateTime CreatedAtUtc,
    long SizeBytes,
    bool IsDeletionMarker,
    string ContentHashSha256,
    bool HasContentBlocks);
