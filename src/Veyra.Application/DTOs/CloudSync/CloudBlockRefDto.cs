namespace Veyra.Application.DTOs.CloudSync;

public sealed record CloudBlockRefDto(
    int Sequence,
    string BlockHash,
    int LengthBytes,
    long StoredSizeBytes);
