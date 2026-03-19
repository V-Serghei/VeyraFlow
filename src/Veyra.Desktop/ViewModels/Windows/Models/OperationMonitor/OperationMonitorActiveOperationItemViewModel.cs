namespace Veyra.Desktop.ViewModels.Windows;

public sealed record OperationMonitorActiveOperationItemViewModel(
    int RepositoryId,
    string Name,
    string StatusText,
    string QueueText,
    bool HasProgress,
    double ProgressPercent,
    string ProgressText,
    string ElapsedText,
    string UpdatedText,
    string ErrorText,
    bool HasError);
