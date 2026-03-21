namespace Veyra.Application.DTOs;

public sealed record CloudUploadBlockItemDto(
    string BlockHash,
    string SourcePath,
    long ContentLength);
