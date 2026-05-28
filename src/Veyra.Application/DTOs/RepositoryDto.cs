namespace Veyra.Application.DTOs;

public sealed record RepositoryDto(
    int Id,
    string Name,
    string? Description,
    int DirectoryId,
    string DirectoryPath,
    IReadOnlyList<string> LinkedFormats,
    bool IsDeleted,
    int FileCount,
    int VersionCount,
    long TotalSizeBytes,
    DateTime? LastScannedAt);
