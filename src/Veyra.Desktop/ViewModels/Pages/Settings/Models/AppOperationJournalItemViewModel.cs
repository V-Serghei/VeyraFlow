namespace Veyra.Desktop.ViewModels.Pages.Settings;

public sealed record AppOperationJournalItemViewModel(
    string TimestampText,
    string Level,
    string Category,
    string Action,
    string ScopeText,
    string Message);
