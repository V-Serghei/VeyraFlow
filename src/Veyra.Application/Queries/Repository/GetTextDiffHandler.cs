using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;

namespace Veyra.Application.Queries.Repository;

public sealed class GetTextDiffHandler(
    IRepositorySnapshotRepository snapshots,
    IFileContentStore contentStore,
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
            var left = await snapshots.GetFileVersionRestoreDataAsync(request.LeftFileVersionId, ct);
            var right = await snapshots.GetFileVersionRestoreDataAsync(request.RightFileVersionId, ct);

            if (left is null || right is null)
                return OperationResult<TextDiffResultDto>.Fail("Одна из версий не найдена.");

            if (!left.RelativePath.Equals(right.RelativePath, StringComparison.OrdinalIgnoreCase))
                return OperationResult<TextDiffResultDto>.Fail("Версии относятся к разным файлам.");

            if (left.IsDeletionMarker || right.IsDeletionMarker)
                return OperationResult<TextDiffResultDto>.Fail("Diff для версии удаления не поддерживается.");

            var extension = left.Extension ?? right.Extension;
            if (string.IsNullOrWhiteSpace(extension) || !TextExtensions.Contains(extension))
                return OperationResult<TextDiffResultDto>.Fail("Формат файла не поддерживает текстовый diff.");

            if ((left.Blocks.Count == 0 && left.SizeBytes > 0) || (right.Blocks.Count == 0 && right.SizeBytes > 0))
                return OperationResult<TextDiffResultDto>.Fail("Для одной из версий отсутствуют блоки данных.");

            await contentStore.RestoreFileAsync(left.Blocks, leftTemp, true, ct);
            await contentStore.RestoreFileAsync(right.Blocks, rightTemp, true, ct);

            var leftLines = await File.ReadAllLinesAsync(leftTemp, ct);
            var rightLines = await File.ReadAllLinesAsync(rightTemp, ct);

            var maxLines = Math.Clamp(request.MaxLines, 200, 20_000);
            var isTruncated = leftLines.Length > maxLines || rightLines.Length > maxLines;

            if (isTruncated)
            {
                leftLines = leftLines.Take(maxLines).ToArray();
                rightLines = rightLines.Take(maxLines).ToArray();
            }

            var lines = BuildLineDiff(leftLines, rightLines, out var added, out var removed);

            var result = new TextDiffResultDto(
                left.RelativePath,
                left.FileVersionId,
                right.FileVersionId,
                added,
                removed,
                isTruncated,
                lines);

            return OperationResult<TextDiffResultDto>.Ok(result);
        }
        catch (Exception ex)
        {
            log.LogError(ex,
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

    private static IReadOnlyList<TextDiffLineDto> BuildLineDiff(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right,
        out int added,
        out int removed)
    {
        var n = left.Count;
        var m = right.Count;

        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                if (left[i] == right[j])
                    lcs[i, j] = lcs[i + 1, j + 1] + 1;
                else
                    lcs[i, j] = Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var lines = new List<TextDiffLineDto>(Math.Max(n, m));

        var li = 0;
        var ri = 0;
        var leftLineNumber = 1;
        var rightLineNumber = 1;

        added = 0;
        removed = 0;

        while (li < n && ri < m)
        {
            if (left[li] == right[ri])
            {
                lines.Add(new TextDiffLineDto("equal", leftLineNumber, rightLineNumber, left[li]));
                li++;
                ri++;
                leftLineNumber++;
                rightLineNumber++;
                continue;
            }

            if (lcs[li + 1, ri] >= lcs[li, ri + 1])
            {
                lines.Add(new TextDiffLineDto("remove", leftLineNumber, null, left[li]));
                li++;
                leftLineNumber++;
                removed++;
            }
            else
            {
                lines.Add(new TextDiffLineDto("add", null, rightLineNumber, right[ri]));
                ri++;
                rightLineNumber++;
                added++;
            }
        }

        while (li < n)
        {
            lines.Add(new TextDiffLineDto("remove", leftLineNumber, null, left[li]));
            li++;
            leftLineNumber++;
            removed++;
        }

        while (ri < m)
        {
            lines.Add(new TextDiffLineDto("add", null, rightLineNumber, right[ri]));
            ri++;
            rightLineNumber++;
            added++;
        }

        return lines;
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
