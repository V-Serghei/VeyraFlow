namespace Veyra.Desktop.Services.Sync;

internal sealed record PendingBlockUploadItem(
    int Index,
    string BlockHash,
    string SourcePath,
    long ContentLength);
