namespace Veyra.Desktop.ViewModels.Windows;

public static class ImageDiffPreviewDefaults
{
    // UX defaults for the interactive image comparison panel.
    // Keep these named because they are shared by multiple compare surfaces.
    public const double SensitivityPercent = 72d;
    public const double SplitPercent = 50d;
    public const double MinSplitPercent = 8d;
    public const double MaxSplitPercent = 92d;
    public const bool ShowRegionBoxes = true;
    public const bool ShowSourceImagePanels = false;
}
