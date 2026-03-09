namespace Veyra.Desktop.ViewModels.Windows;

public sealed class SnapshotDiffRowItemViewModel
{
    public string LeftLineNumber { get; init; } = string.Empty;
    public string LeftMarker { get; init; } = string.Empty;
    public string LeftText { get; init; } = string.Empty;
    public string LeftBackground { get; init; } = "#1B2C42";
    public string LeftMarkerForeground { get; init; } = "#8FA5BF";

    public string RightLineNumber { get; init; } = string.Empty;
    public string RightMarker { get; init; } = string.Empty;
    public string RightText { get; init; } = string.Empty;
    public string RightBackground { get; init; } = "#1B2C42";
    public string RightMarkerForeground { get; init; } = "#8FA5BF";

    public static SnapshotDiffRowItemViewModel CreateSeparator() => new()
    {
        LeftLineNumber = "....",
        LeftMarker = "~",
        LeftText = "hunk split",
        LeftBackground = "#263147",
        LeftMarkerForeground = "#9BC0FF",
        RightLineNumber = "....",
        RightMarker = "~",
        RightText = "hunk split",
        RightBackground = "#263147",
        RightMarkerForeground = "#9BC0FF"
    };
}