using System.Text;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;

namespace Veyra.Application.Queries.Repository;

public sealed class GetFileVersionTextContentHandler(
    IRepositorySnapshotRepository snapshots,
    IFileContentStore contentStore,
    ILogger<GetFileVersionTextContentHandler> log)
    : IRequestHandler<GetFileVersionTextContentQuery, OperationResult<FileVersionTextContentDto>>
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".xml", ".yml", ".yaml", ".ini", ".toml", ".log",
        ".cs", ".js", ".ts", ".java", ".py", ".rs", ".go", ".c", ".cpp", ".h", ".hpp",
        ".html", ".css", ".sql", ".xaml", ".axaml"
    };

    public async Task<OperationResult<FileVersionTextContentDto>> Handle(GetFileVersionTextContentQuery request, CancellationToken ct)
    {
        if (request.FileVersionId <= 0)
            return OperationResult<FileVersionTextContentDto>.Fail("File version is not selected.");

        var maxBytes = Math.Clamp(request.MaxBytes, 16 * 1024, 8 * 1024 * 1024);
        var tempDir = Path.Combine(Path.GetTempPath(), "VeyraFlow", "preview");
        Directory.CreateDirectory(tempDir);

        var tempPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}.preview.tmp");

        try
        {
            var restoreData = await snapshots.GetFileVersionRestoreDataAsync(request.FileVersionId, ct);
            if (restoreData is null)
                return OperationResult<FileVersionTextContentDto>.Fail("Selected version was not found.");

            if (restoreData.IsDeletionMarker)
                return OperationResult<FileVersionTextContentDto>.Fail("Deletion version cannot be opened as text.");

            var extension = restoreData.Extension;
            if (string.IsNullOrWhiteSpace(extension) || !TextExtensions.Contains(extension))
                return OperationResult<FileVersionTextContentDto>.Fail("Selected version is not a supported text format.");

            if (restoreData.Blocks.Count == 0 && restoreData.SizeBytes > 0)
                return OperationResult<FileVersionTextContentDto>.Fail("Version blocks are missing.");

            await contentStore.RestoreFileAsync(restoreData.Blocks, tempPath, overwriteExisting: true, ct);

            var fileInfo = new FileInfo(tempPath);
            var isTruncated = fileInfo.Length > maxBytes;
            var readLength = (int)Math.Min(fileInfo.Length, maxBytes);

            byte[] bytes;
            await using (var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true))
            {
                bytes = new byte[readLength];
                var offset = 0;

                while (offset < readLength)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(offset, readLength - offset), ct);
                    if (read == 0)
                        break;

                    offset += read;
                }

                if (offset < readLength)
                    Array.Resize(ref bytes, offset);
            }

            var content = DecodeText(bytes);
            var dto = new FileVersionTextContentDto(
                restoreData.FileVersionId,
                restoreData.RelativePath,
                content,
                isTruncated,
                restoreData.SizeBytes);

            return OperationResult<FileVersionTextContentDto>.Ok(dto);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to load text content for file version {FileVersionId}", request.FileVersionId);
            return OperationResult<FileVersionTextContentDto>.Fail(FormatReadError(ex));
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length == 0)
            return string.Empty;

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        return Encoding.UTF8.GetString(bytes);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static string FormatReadError(Exception ex)
    {
        var text = ex.ToString();

        if (text.Contains("native block format", StringComparison.OrdinalIgnoreCase)
            || text.Contains("native block hash", StringComparison.OrdinalIgnoreCase))
        {
            return "Version content cannot be opened right now: native block restore is unavailable. Rebuild/update veyra_core and run Reindex data.";
        }

        if (text.Contains("Block file not found", StringComparison.OrdinalIgnoreCase)
            || text.Contains("missing", StringComparison.OrdinalIgnoreCase))
        {
            return "Version content cannot be opened because some block files are missing. Run Repair data or Reindex data.";
        }

        if (text.Contains("decryption", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Artifact key", StringComparison.OrdinalIgnoreCase))
        {
            return "Version content cannot be opened because encrypted blocks cannot be decrypted with the current key set.";
        }

        return ex.Message;
    }
}
