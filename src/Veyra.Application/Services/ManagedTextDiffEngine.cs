using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.DTOs;

namespace Veyra.Application.Services;

public sealed class ManagedTextDiffEngine : ITextDiffEngine
{
    public async Task<TextDiffComputationDto> BuildDiffAsync(
        string leftFilePath,
        string rightFilePath,
        int maxLines,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leftFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightFilePath);

        var normalizedMaxLines = Math.Clamp(maxLines, 200, 20_000);

        var leftLines = await File.ReadAllLinesAsync(leftFilePath, ct);
        var rightLines = await File.ReadAllLinesAsync(rightFilePath, ct);

        var isTruncated = leftLines.Length > normalizedMaxLines || rightLines.Length > normalizedMaxLines;
        if (isTruncated)
        {
            leftLines = leftLines.Take(normalizedMaxLines).ToArray();
            rightLines = rightLines.Take(normalizedMaxLines).ToArray();
        }

        var lines = BuildLineDiff(leftLines, rightLines, out var added, out var removed);
        var hunks = TextDiffHunkBuilder.Build(lines, 3);
        return new TextDiffComputationDto(added, removed, isTruncated, lines, hunks);
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
}

