namespace Veyra.Application.DTOs;

public sealed record RepositoryVersionPlanningFileStateDto(
    string RelativePath,
    bool HasCurrent,
    long CurrentSizeBytes,
    string? CurrentContentHashSha256,
    bool HasPrevious,
    long PreviousSizeBytes,
    string? PreviousContentHashSha256,
    bool HasLatestVersion,
    bool LatestIsDeletionMarker,
    long LatestSizeBytes,
    bool LatestHasBlocks);
