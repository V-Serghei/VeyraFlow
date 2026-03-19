namespace Veyra.Application.DTOs;

public sealed record StoredFileBlockDto(
    int Sequence,
    string BlockHashBlake3,
    int LengthBytes,
    long StoredSizeBytes);
