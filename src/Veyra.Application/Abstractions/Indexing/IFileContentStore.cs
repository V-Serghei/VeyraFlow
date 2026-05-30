using Veyra.Application.DTOs;
using Veyra.Application.DTOs.FileVersions;

namespace Veyra.Application.Abstractions.Indexing;

public interface IFileContentStore
{
    public Task<StoredFileContentDto> StoreFileAsync(string filePath, CancellationToken ct = default);

    public Task<long> RestoreFileAsync(
        IReadOnlyList<StoredFileBlockDto> blocks,
        string targetPath,
        bool overwriteExisting,
        string? expectedContentHash = null,
        CancellationToken ct = default);

    public Task<IReadOnlyList<string>> FindMissingBlocksAsync(
        IReadOnlyCollection<string> blockStorageKeys,
        CancellationToken ct = default);
}
