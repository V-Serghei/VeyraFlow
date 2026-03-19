using System.Collections.Generic;
using System.Linq;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed class SnapshotDiffRowItemViewModel
{
    public const string RowTypeHunk = "hunk";
    public const string RowTypeContent = "content";
    public const string RowTypeContextFold = "context_fold";

    public string RowType { get; init; } = RowTypeContent;
    public string DiffKind { get; init; } = "context";
    public int HunkSequence { get; init; }

    public bool IsHunkHeader => string.Equals(RowType, RowTypeHunk, System.StringComparison.Ordinal);
    public bool IsContentRow => string.Equals(RowType, RowTypeContent, System.StringComparison.Ordinal);
    public bool IsContextFoldRow => string.Equals(RowType, RowTypeContextFold, System.StringComparison.Ordinal);

    public bool IsPrimaryChange => DiffKind is "add" or "remove" or "modified";
    public bool IsAddKind => string.Equals(DiffKind, "add", System.StringComparison.Ordinal);
    public bool IsRemoveKind => string.Equals(DiffKind, "remove", System.StringComparison.Ordinal);
    public bool IsModifiedKind => string.Equals(DiffKind, "modified", System.StringComparison.Ordinal);
    public bool IsContextKind => string.Equals(DiffKind, "context", System.StringComparison.Ordinal);

    public string LeftGutter => string.IsNullOrWhiteSpace(LeftLineNumber) ? "    " : LeftLineNumber;
    public string RightGutter => string.IsNullOrWhiteSpace(RightLineNumber) ? "    " : RightLineNumber;

    public string KindLabel => DiffKind switch
    {
        "add" => "add",
        "remove" => "remove",
        "modified" => "mod",
        _ => "ctx"
    };

    public string HunkHeader { get; init; } = string.Empty;
    public string ContextFoldLabel { get; init; } = string.Empty;

    public IReadOnlyList<SnapshotDiffRowItemViewModel> HiddenContextRows { get; init; } = [];

    public string LeftLineNumber { get; init; } = string.Empty;
    public string LeftMarker { get; init; } = string.Empty;
    public string LeftBackground { get; init; } = "#13253A";
    public string LeftMarkerForeground { get; init; } = "#98B2CE";

    public string RightLineNumber { get; init; } = string.Empty;
    public string RightMarker { get; init; } = string.Empty;
    public string RightBackground { get; init; } = "#13253A";
    public string RightMarkerForeground { get; init; } = "#98B2CE";

    public IReadOnlyList<SnapshotDiffTextSegmentViewModel> LeftSegments { get; init; } = [];
    public IReadOnlyList<SnapshotDiffTextSegmentViewModel> RightSegments { get; init; } = [];

    public string LeftText => string.Concat(LeftSegments.Select(x => x.Text));
    public string RightText => string.Concat(RightSegments.Select(x => x.Text));

    public static SnapshotDiffRowItemViewModel CreateHunkHeader(
        int hunkSequence,
        string oldRange,
        string newRange,
        string kind) => new()
    {
        RowType = RowTypeHunk,
        HunkSequence = hunkSequence,
        DiffKind = NormalizeKind(kind),
        HunkHeader = $"@@ {oldRange} -> {newRange} [{kind}] @@"
    };

    public static SnapshotDiffRowItemViewModel CreateContextFold(
        int hunkSequence,
        int hiddenLines,
        IReadOnlyList<SnapshotDiffRowItemViewModel> hiddenRows) => new()
    {
        RowType = RowTypeContextFold,
        HunkSequence = hunkSequence,
        DiffKind = "context",
        ContextFoldLabel = $"Show {hiddenLines} unchanged lines",
        HiddenContextRows = hiddenRows
    };

    public static SnapshotDiffRowItemViewModel CreateContentRow(
        int hunkSequence,
        string diffKind,
        string leftLineNumber,
        string leftMarker,
        string leftBackground,
        string leftMarkerForeground,
        IReadOnlyList<SnapshotDiffTextSegmentViewModel> leftSegments,
        string rightLineNumber,
        string rightMarker,
        string rightBackground,
        string rightMarkerForeground,
        IReadOnlyList<SnapshotDiffTextSegmentViewModel> rightSegments) => new()
    {
        RowType = RowTypeContent,
        HunkSequence = hunkSequence,
        DiffKind = NormalizeKind(diffKind),
        LeftLineNumber = leftLineNumber,
        LeftMarker = leftMarker,
        LeftBackground = leftBackground,
        LeftMarkerForeground = leftMarkerForeground,
        LeftSegments = leftSegments,
        RightLineNumber = rightLineNumber,
        RightMarker = rightMarker,
        RightBackground = rightBackground,
        RightMarkerForeground = rightMarkerForeground,
        RightSegments = rightSegments
    };

    private static string NormalizeKind(string? value)
    {
        if (string.Equals(value, "add", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "added", System.StringComparison.OrdinalIgnoreCase))
            return "add";

        if (string.Equals(value, "remove", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "removed", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "deleted", System.StringComparison.OrdinalIgnoreCase))
            return "remove";

        if (string.Equals(value, "modified", System.StringComparison.OrdinalIgnoreCase))
            return "modified";

        return "context";
    }
}
