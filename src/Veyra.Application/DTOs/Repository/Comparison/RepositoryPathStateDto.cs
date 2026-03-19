namespace Veyra.Application.DTOs;

public sealed record RepositoryPathStateDto(
    string RelativePath,
    string Name,
    long SizeBytes,
    DateTime LastWriteUtc,
    string? ContentHashSha256);
