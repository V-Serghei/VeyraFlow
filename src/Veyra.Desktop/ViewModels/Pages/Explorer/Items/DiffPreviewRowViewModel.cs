namespace Veyra.Desktop.ViewModels.Pages.Explorer;

public sealed class DiffPreviewRowViewModel
{
    public bool IsHunkHeader { get; init; }
    public bool IsContentRow => !IsHunkHeader;
    public string HunkHeader { get; init; } = string.Empty;

    public string KindBadge { get; init; } = "=";

    public string LeftLineNumber { get; init; } = string.Empty;
    public string LeftMarker { get; init; } = string.Empty;
    public string LeftText { get; init; } = string.Empty;
    public string LeftBackground { get; init; } = "#1A2F47";
    public string LeftMarkerForeground { get; init; } = "#9CB4CF";

    public string RightLineNumber { get; init; } = string.Empty;
    public string RightMarker { get; init; } = string.Empty;
    public string RightText { get; init; } = string.Empty;
    public string RightBackground { get; init; } = "#1A2F47";
    public string RightMarkerForeground { get; init; } = "#9CB4CF";

    public static DiffPreviewRowViewModel CreateHunkHeader(string oldRange, string newRange, string kind) => new()
    {
        IsHunkHeader = true,
        KindBadge = "@@",
        HunkHeader = $"@@ {oldRange} -> {newRange} [{kind}] @@"
    };
}
