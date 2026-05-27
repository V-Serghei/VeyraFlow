namespace Veyra.Application.DTOs;

public sealed record RepositoryCloudRepairResultDto(
    bool Success,
    int ReferencedBlocks,
    int AlreadyPresentBlocks,
    int UploadedBlocks,
    int MissingLocalBlocks,
    int FailedUploads,
    string? ErrorMessage = null);
