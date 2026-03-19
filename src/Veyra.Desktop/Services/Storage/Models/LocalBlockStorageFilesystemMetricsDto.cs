namespace Veyra.Desktop.Services.Storage.Models;

public sealed record LocalBlockStorageFilesystemMetricsDto(
    long ManagedFileCount,
    long NativeFileCount,
    long OtherFileCount,
    long ManagedBytes,
    long NativeBytes,
    long OtherBytes);
