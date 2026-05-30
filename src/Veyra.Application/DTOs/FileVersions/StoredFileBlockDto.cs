namespace Veyra.Application.DTOs.FileVersions;

public sealed record StoredFileBlockDto(
    int Sequence,
    string BlockStorageKey,
    int LengthBytes,
    long StoredSizeBytes);
