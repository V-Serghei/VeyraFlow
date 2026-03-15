namespace Veyra.Desktop.ViewModels.Windows;

public sealed record AudioChangedSegmentItemViewModel(
    string SegmentLabel,
    string RangeLabel,
    string DurationLabel,
    string IntensityLabel,
    double StartSeconds,
    double DurationSeconds);
