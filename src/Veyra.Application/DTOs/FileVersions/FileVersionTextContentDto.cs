namespace Veyra.Application.DTOs;

public sealed record FileVersionTextContentDto(
    long FileVersionId,
    string RelativePath,
    string Content,
    bool IsTruncated,
    long SizeBytes);
