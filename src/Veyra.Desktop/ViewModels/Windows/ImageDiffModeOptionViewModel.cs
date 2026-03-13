using CommunityToolkit.Mvvm.ComponentModel;
using Veyra.Desktop.Services.Preview;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class ImageDiffModeOptionViewModel : ObservableObject
{
    public ImageDiffModeOptionViewModel(ImageDiffVisualizationMode mode, string label)
    {
        Mode = mode;
        Label = label;
    }

    public ImageDiffVisualizationMode Mode { get; }
    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;
}
