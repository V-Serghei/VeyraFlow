using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;

namespace Veyra.Application.Queries.Repository;

public sealed class GetTextDiffHandler(
    IRepositorySnapshotRepository snapshots,
    IFileContentStore contentStore,
    ITextDiffEngine diffEngine,
    ILogger<GetTextDiffHandler> log)
    : IRequestHandler<GetTextDiffQuery, OperationResult<TextDiffResultDto>>
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".xml", ".yml", ".yaml", ".ini", ".toml", ".log",
        ".cs", ".js", ".ts", ".java", ".py", ".rs", ".go", ".c", ".cpp", ".h", ".hpp",
        ".html", ".css", ".sql", ".xaml", ".axaml"
    };

    public async Task<OperationResult<TextDiffResultDto>> Handle(GetTextDiffQuery request, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "VeyraFlow", "diff");
        Directory.CreateDirectory(tempDir);

        var leftTemp = Path.Combine(tempDir, $"{Guid.NewGuid():N}.left.tmp");
        var rightTemp = Path.Combine(tempDir, $"{Guid.NewGuid():N}.right.tmp");

        try
        {
            var maxLines = Math.Clamp(request.MaxLines, 200, 20_000);

            var left = await snapshots.GetFileVersionRestoreDataAsync(request.LeftFileVersionId, ct);
            var right = await snapshots.GetFileVersionRestoreDataAsync(request.RightFileVersionId, ct);

            if (left is null || right is null)
                return OperationResult<TextDiffResultDto>.Fail("One of the selected versions was not found.");

            if (!left.RelativePath.Equals(right.RelativePath, StringComparison.OrdinalIgnoreCase))
                return OperationResult<TextDiffResultDto>.Fail("Selected versions belong to different files.");

            if (left.IsDeletionMarker || right.IsDeletionMarker)
                return OperationResult<TextDiffResultDto>.Fail("Diff preview is not available for deletion versions.");

            var extension = left.Extension ?? right.Extension;
            if (string.IsNullOrWhiteSpace(extension) || !TextExtensions.Contains(extension))
                return OperationResult<TextDiffResultDto>.Fail("File format is not supported for text diff.");

            var cached = await snapshots.GetStoredTextDiffAsync(request.LeftFileVersionId, request.RightFileVersionId, maxLines, ct);
            if (cached is not null)
                return OperationResult<TextDiffResultDto>.Ok(cached);

            if ((left.Blocks.Count == 0 && left.SizeBytes > 0) || (right.Blocks.Count == 0 && right.SizeBytes > 0))
                return OperationResult<TextDiffResultDto>.Fail("Blocks are missing for one of the selected versions.");

            await contentStore.RestoreFileAsync(left.Blocks, leftTemp, true, ct);
            await contentStore.RestoreFileAsync(right.Blocks, rightTemp, true, ct);

            var computed = await diffEngine.BuildDiffAsync(leftTemp, rightTemp, maxLines, ct);

            var result = new TextDiffResultDto(
                left.RelativePath,
                left.FileVersionId,
                right.FileVersionId,
                computed.AddedLines,
                computed.RemovedLines,
                computed.IsTruncated,
                computed.Lines);

            try
            {
                await snapshots.SaveStoredTextDiffAsync(result, maxLines, ct);
            }
            catch (Exception cacheEx)
            {
                log.LogWarning(
                    cacheEx,
                    "Failed to persist text diff cache. LeftVersion {LeftVersion}. RightVersion {RightVersion}",
                    request.LeftFileVersionId,
                    request.RightFileVersionId);
            }

            return OperationResult<TextDiffResultDto>.Ok(result);
        }
        catch (Exception ex)
        {
            log.LogError(
                ex,
                "Failed to build text diff. LeftVersion {LeftVersion}. RightVersion {RightVersion}",
                request.LeftFileVersionId,
                request.RightFileVersionId);

            return OperationResult<TextDiffResultDto>.Fail(ex.Message);
        }
        finally
        {
            TryDelete(leftTemp);
            TryDelete(rightTemp);
        }
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
}
