namespace Veyra.Application.DTOs;

public sealed record RepositoryPendingChangeEntryDto(
    string RelativePath,
    string Name,
    string ChangeKind,
    long CurrentSizeBytes,
    long BaselineSizeBytes,
    DateTime CurrentLastWriteUtc,
    DateTime? BaselineLastWriteUtc);
