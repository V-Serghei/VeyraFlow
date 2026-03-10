using Avalonia.Media;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed class WordSemanticDiffRowViewModel
{
    public bool IsHunkHeader { get; init; }
    public bool IsContentRow => !IsHunkHeader;
    public string HunkHeader { get; init; } = string.Empty;

    public bool IsChanged { get; init; }
    public string KindBadge { get; init; } = "=";

    public string LeftLineNumber { get; init; } = string.Empty;
    public string LeftMarker { get; init; } = string.Empty;
    public string LeftText { get; init; } = string.Empty;
    public FontWeight LeftFontWeight { get; init; } = FontWeight.Normal;
    public FontStyle LeftFontStyle { get; init; } = FontStyle.Normal;
    public string LeftFontFamily { get; init; } = "Calibri";
    public double LeftFontSize { get; init; } = 14d;
    public string LeftForeground { get; init; } = "#111827";
    public string LeftTextBackground { get; init; } = "Transparent";
    public string LeftBackground { get; init; } = "#FFFFFF";
    public string LeftBorderBrush { get; init; } = "#E3E8EF";
    public string LeftMarkerForeground { get; init; } = "#9BB5D1";
    public string LeftStyleTag { get; init; } = string.Empty;
    public string LeftStyleTooltip { get; init; } = string.Empty;

    public string RightLineNumber { get; init; } = string.Empty;
    public string RightMarker { get; init; } = string.Empty;
    public string RightText { get; init; } = string.Empty;
    public FontWeight RightFontWeight { get; init; } = FontWeight.Normal;
    public FontStyle RightFontStyle { get; init; } = FontStyle.Normal;
    public string RightFontFamily { get; init; } = "Calibri";
    public double RightFontSize { get; init; } = 14d;
    public string RightForeground { get; init; } = "#111827";
    public string RightTextBackground { get; init; } = "Transparent";
    public string RightBackground { get; init; } = "#FFFFFF";
    public string RightBorderBrush { get; init; } = "#E3E8EF";
    public string RightMarkerForeground { get; init; } = "#9BB5D1";
    public string RightStyleTag { get; init; } = string.Empty;
    public string RightStyleTooltip { get; init; } = string.Empty;

    public bool HasLeftStyleTag => !string.IsNullOrWhiteSpace(LeftStyleTag);
    public bool HasRightStyleTag => !string.IsNullOrWhiteSpace(RightStyleTag);
    public bool HasLeftStyleTooltip => !string.IsNullOrWhiteSpace(LeftStyleTooltip);
    public bool HasRightStyleTooltip => !string.IsNullOrWhiteSpace(RightStyleTooltip);

    public static WordSemanticDiffRowViewModel CreateHunkHeader(string oldRange, string newRange, string kind)
        => new()
        {
            IsHunkHeader = true,
            KindBadge = "@@",
            HunkHeader = $"@@ {oldRange} -> {newRange} [{kind}] @@"
        };
}
