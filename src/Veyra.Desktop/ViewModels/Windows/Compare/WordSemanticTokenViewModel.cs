using Avalonia;
using Avalonia.Media;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed class WordSemanticTokenViewModel
{
    public string Text { get; init; } = string.Empty;
    public bool IsChanged { get; init; }
    public string Background { get; init; } = "Transparent";
    public string Tooltip { get; init; } = string.Empty;

    public string FontFamily { get; init; } = "Calibri";
    public double FontSize { get; init; } = 14d;
    public FontWeight FontWeight { get; init; } = FontWeight.Normal;
    public FontStyle FontStyle { get; init; } = FontStyle.Normal;
    public string Foreground { get; init; } = "#111827";
    public TextDecorationCollection TextDecorations { get; init; } = [];
    public Thickness TextMargin { get; init; } = new(0);
    public string OutlineBrush { get; init; } = "Transparent";
    public Thickness OutlineThickness { get; init; } = new(0);

    public bool HasTooltip => !string.IsNullOrWhiteSpace(Tooltip);
}
