namespace Veyra.Application.DTOs.OperationJournal;

public sealed record OperationJournalEntryDto(
    long Id,
    DateTime OccurredAtUtc,
    string Level,
    string Category,
    string Action,
    int? RepositoryId,
    string? Username,
    string Message,
    string? Details);
