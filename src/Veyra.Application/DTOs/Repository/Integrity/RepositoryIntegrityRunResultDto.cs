namespace Veyra.Application.DTOs;

public sealed record RepositoryIntegrityRunResultDto(
    int RepositoryId,
    bool RepairAttempted,
    DateTime StartedAtUtc,
    DateTime FinishedAtUtc,
    int TotalBlockReferences,
    int UniqueBlockCount,
    int VerifiedBlockCount,
    int MissingBlockCount,
    int CorruptedBlockCount,
    int RepairedBlockCount,
    int UnresolvedIssueCount,
    IReadOnlyList<RepositoryIntegrityIssueDto> Issues,
    string Summary);
