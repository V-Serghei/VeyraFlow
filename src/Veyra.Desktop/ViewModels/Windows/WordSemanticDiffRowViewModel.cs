using System.Collections.Generic;
using Avalonia;
using Avalonia.Layout;
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
    public string LeftBackground { get; init; } = "Transparent";
    public string LeftBorderBrush { get; init; } = "Transparent";
    public Thickness LeftBorderThickness { get; init; } = new(0);
    public string LeftMarkerForeground { get; init; } = "#9BB5D1";
    public string LeftStyleTag { get; init; } = string.Empty;
    public string LeftStyleTooltip { get; init; } = string.Empty;
    public Thickness LeftParagraphMargin { get; init; } = new(0);
    public HorizontalAlignment LeftParagraphAlignment { get; init; } = HorizontalAlignment.Left;
    public TextAlignment LeftTextAlignment { get; init; } = TextAlignment.Left;
    public TextDecorationCollection LeftTextDecorations { get; init; } = [];
    public Thickness LeftTextMargin { get; init; } = new(0);
    public IReadOnlyList<WordSemanticTokenViewModel> LeftTokens { get; init; } = [];

    public string RightLineNumber { get; init; } = string.Empty;
    public string RightMarker { get; init; } = string.Empty;
    public string RightText { get; init; } = string.Empty;
    public FontWeight RightFontWeight { get; init; } = FontWeight.Normal;
    public FontStyle RightFontStyle { get; init; } = FontStyle.Normal;
    public string RightFontFamily { get; init; } = "Calibri";
    public double RightFontSize { get; init; } = 14d;
    public string RightForeground { get; init; } = "#111827";
    public string RightTextBackground { get; init; } = "Transparent";
    public string RightBackground { get; init; } = "Transparent";
    public string RightBorderBrush { get; init; } = "Transparent";
    public Thickness RightBorderThickness { get; init; } = new(0);
    public string RightMarkerForeground { get; init; } = "#9BB5D1";
    public string RightStyleTag { get; init; } = string.Empty;
    public string RightStyleTooltip { get; init; } = string.Empty;
    public Thickness RightParagraphMargin { get; init; } = new(0);
    public HorizontalAlignment RightParagraphAlignment { get; init; } = HorizontalAlignment.Left;
    public TextAlignment RightTextAlignment { get; init; } = TextAlignment.Left;
    public TextDecorationCollection RightTextDecorations { get; init; } = [];
    public Thickness RightTextMargin { get; init; } = new(0);
    public IReadOnlyList<WordSemanticTokenViewModel> RightTokens { get; init; } = [];

    public bool HasLeftStyleTag => !string.IsNullOrWhiteSpace(LeftStyleTag);
    public bool HasRightStyleTag => !string.IsNullOrWhiteSpace(RightStyleTag);
    public bool HasLeftStyleTooltip => !string.IsNullOrWhiteSpace(LeftStyleTooltip);
    public bool HasRightStyleTooltip => !string.IsNullOrWhiteSpace(RightStyleTooltip);
    public bool HasLeftTokens => LeftTokens.Count > 0;
    public bool HasRightTokens => RightTokens.Count > 0;
    public bool UseLeftTokenLayout => LeftTokens.Count > 0;
    public bool UseRightTokenLayout => RightTokens.Count > 0;
    public bool UseLeftPlainLayout => LeftTokens.Count == 0;
    public bool UseRightPlainLayout => RightTokens.Count == 0;

    public static WordSemanticDiffRowViewModel CreateHunkHeader(string oldRange, string newRange, string kind)
        => new()
        {
            IsHunkHeader = true,
            KindBadge = "@@",
            HunkHeader = $"@@ {oldRange} -> {newRange} [{kind}] @@"
        };
}
