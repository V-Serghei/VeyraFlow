using Avalonia;
using Avalonia.Media;

namespace Veyra.Desktop.ViewModels.Pages.Explorer;

public sealed class DiffPreviewTokenViewModel
{
    public string Text { get; init; } = string.Empty;
    public string Background { get; init; } = "Transparent";
    public string Foreground { get; init; } = "#F5F8FF";
    public FontWeight FontWeight { get; init; } = FontWeight.Normal;
    public Thickness Padding { get; init; } = new(0);
    public string Tooltip { get; init; } = string.Empty;

    public bool HasTooltip => !string.IsNullOrWhiteSpace(Tooltip);
}
