namespace Veyra.Application.DTOs.CloudSync;

public sealed record CloudUploadBlockItemDto(
    string BlockHash,
    string SourcePath,
    long ContentLength);
