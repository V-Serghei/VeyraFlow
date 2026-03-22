namespace Veyra.Application.DTOs;

public sealed record StoredFileBlockDto(
    int Sequence,
    string BlockStorageKey,
    int LengthBytes,
    long StoredSizeBytes);
