namespace Veyra.Application.DTOs.Repository.Integrity;

public sealed record RepositoryIntegrityIssueDto(
    string Code,
    string Severity,
    string BlockHash,
    long FileVersionId,
    string RelativePath,
    string Details);
