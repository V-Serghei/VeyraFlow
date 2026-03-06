using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Indexing;

public interface IFileContentStore
{
    Task<StoredFileContentDto> StoreFileAsync(string filePath, CancellationToken ct = default);

    Task<long> RestoreFileAsync(
        IReadOnlyList<StoredFileBlockDto> blocks,
        string targetPath,
        bool overwriteExisting,
        CancellationToken ct = default);
}
