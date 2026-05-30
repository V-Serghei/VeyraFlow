namespace Veyra.Application.DTOs.Repository.Bundles;

public sealed record RepositoryBundleValidationResultDto(
    bool IsValid,
    int BundleFormatVersion,
    string Message,
    IReadOnlyList<string> Warnings);
