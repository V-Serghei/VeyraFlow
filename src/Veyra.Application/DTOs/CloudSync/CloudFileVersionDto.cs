namespace Veyra.Application.DTOs.CloudSync;

public sealed record CloudFileVersionDto(
    string RelativePath,
    long FileVersionId,
    string ContentHashSha256,
    long SizeBytes,
    bool IsDeletionMarker,
    DateTime CreatedAtUtc,
    IReadOnlyList<CloudBlockRefDto> Blocks);
