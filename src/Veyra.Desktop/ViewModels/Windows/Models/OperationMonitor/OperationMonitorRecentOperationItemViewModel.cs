namespace Veyra.Desktop.ViewModels.Windows;

public sealed record OperationMonitorRecentOperationItemViewModel(
    long Id,
    string TimestampText,
    string CategoryText,
    string ActionText,
    string Message,
    string DurationText,
    string LevelText);
