namespace Veyra.Application.DTOs;

public sealed record RepositoryBundleValidationResultDto(
    bool IsValid,
    int BundleFormatVersion,
    string Message,
    IReadOnlyList<string> Warnings);
