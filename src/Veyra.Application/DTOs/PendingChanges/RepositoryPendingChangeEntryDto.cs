namespace Veyra.Application.DTOs.PendingChanges;

public sealed record RepositoryPendingChangeEntryDto(
    string RelativePath,
    string Name,
    string ChangeKind,
    long CurrentSizeBytes,
    long BaselineSizeBytes,
    DateTime CurrentLastWriteUtc,
    DateTime? BaselineLastWriteUtc);
