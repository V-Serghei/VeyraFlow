namespace Veyra.Application.DTOs.TextDiff;

public static class TextDiffHunkBuilder
{
    public static IReadOnlyList<TextDiffHunkDto> Build(
        IReadOnlyList<TextDiffLineDto> lines,
        int contextLines = 3)
    {
        if (lines.Count == 0)
            return Array.Empty<TextDiffHunkDto>();

        var normalizedContext = Math.Clamp(contextLines, 0, 20);
        var changedIndexes = new List<int>();

        for (var i = 0; i < lines.Count; i++)
        {
            var kind = NormalizeKind(lines[i].Kind);
            if (kind is "add" or "remove")
                changedIndexes.Add(i);
        }

        if (changedIndexes.Count == 0)
            return Array.Empty<TextDiffHunkDto>();

        var ranges = new List<(int Start, int End)>();
        var segStart = changedIndexes[0];
        var segEnd = segStart;

        for (var i = 1; i < changedIndexes.Count; i++)
        {
            var idx = changedIndexes[i];
            if (idx == segEnd + 1)
            {
                segEnd = idx;
                continue;
            }

            ranges.Add((Math.Max(0, segStart - normalizedContext), Math.Min(lines.Count - 1, segEnd + normalizedContext)));
            segStart = idx;
            segEnd = idx;
        }

        ranges.Add((Math.Max(0, segStart - normalizedContext), Math.Min(lines.Count - 1, segEnd + normalizedContext)));

        var merged = new List<(int Start, int End)>();
        foreach (var range in ranges.OrderBy(r => r.Start))
        {
            if (merged.Count == 0)
            {
                merged.Add(range);
                continue;
            }

            var last = merged[^1];
            if (range.Start <= last.End + 1)
            {
                merged[^1] = (last.Start, Math.Max(last.End, range.End));
                continue;
            }

            merged.Add(range);
        }

        var result = new List<TextDiffHunkDto>(merged.Count);

        for (var i = 0; i < merged.Count; i++)
        {
            var (start, end) = merged[i];

            var oldStart = lines
                .Skip(start)
                .Take(end - start + 1)
                .Select(l => l.LeftLineNumber)
                .FirstOrDefault(v => v.HasValue) ?? 0;

            var newStart = lines
                .Skip(start)
                .Take(end - start + 1)
                .Select(l => l.RightLineNumber)
                .FirstOrDefault(v => v.HasValue) ?? 0;

            var oldCount = 0;
            var newCount = 0;
            var hasAdd = false;
            var hasRemove = false;

            for (var j = start; j <= end; j++)
            {
                var line = lines[j];
                var kind = NormalizeKind(line.Kind);

                if (kind != "add" && line.LeftLineNumber.HasValue)
                    oldCount++;

                if (kind != "remove" && line.RightLineNumber.HasValue)
                    newCount++;

                if (kind == "add")
                    hasAdd = true;

                if (kind == "remove")
                    hasRemove = true;
            }

            var changeKind = hasAdd && hasRemove
                ? "modified"
                : hasAdd
                    ? "added"
                    : hasRemove
                        ? "removed"
                        : "modified";

            result.Add(new TextDiffHunkDto(
                Sequence: i,
                StartLineSequence: start,
                EndLineSequence: end,
                OldStartLine: oldStart,
                OldLineCount: oldCount,
                NewStartLine: newStart,
                NewLineCount: newCount,
                ChangeKind: changeKind));
        }

        return result;
    }

    private static string NormalizeKind(string? kind)
    {
        if (string.Equals(kind, "add", StringComparison.OrdinalIgnoreCase))
            return "add";

        if (string.Equals(kind, "remove", StringComparison.OrdinalIgnoreCase))
            return "remove";

        return "equal";
    }
}
