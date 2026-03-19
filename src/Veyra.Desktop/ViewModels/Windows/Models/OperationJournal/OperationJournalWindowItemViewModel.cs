namespace Veyra.Desktop.ViewModels.Windows;

public sealed record OperationJournalWindowItemViewModel(
    long Id,
    System.DateTime OccurredAtLocal,
    string TimestampText,
    string Level,
    string LevelLabel,
    string Category,
    string CategoryLabel,
    string Action,
    string ScopeText,
    string Message,
    string DetailsText,
    string SearchText);
