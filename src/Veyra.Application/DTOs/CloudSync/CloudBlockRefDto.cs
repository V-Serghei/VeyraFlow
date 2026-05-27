namespace Veyra.Application.DTOs;

public sealed record CloudBlockRefDto(
    int Sequence,
    string BlockHash,
    int LengthBytes,
    long StoredSizeBytes);
