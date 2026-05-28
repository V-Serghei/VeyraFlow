using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Indexing;

public interface IFileContentStore
{
    Task<StoredFileContentDto> StoreFileAsync(string filePath, CancellationToken ct = default);

    Task<long> RestoreFileAsync(
        IReadOnlyList<StoredFileBlockDto> blocks,
        string targetPath,
        bool overwriteExisting,
        string? expectedContentHash = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<string>> FindMissingBlocksAsync(
        IReadOnlyCollection<string> blockStorageKeys,
        CancellationToken ct = default);
}
