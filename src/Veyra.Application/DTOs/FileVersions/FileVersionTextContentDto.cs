namespace Veyra.Application.DTOs.FileVersions;

public sealed record FileVersionTextContentDto(
    long FileVersionId,
    string RelativePath,
    string Content,
    bool IsTruncated,
    long SizeBytes);
