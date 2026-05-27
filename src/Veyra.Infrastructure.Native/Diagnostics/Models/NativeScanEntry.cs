namespace Veyra.Infrastructure.Native.Diagnostics;

internal sealed record NativeScanEntry(
    string? RelativePath,
    bool IsDirectory,
    long SizeBytes);
