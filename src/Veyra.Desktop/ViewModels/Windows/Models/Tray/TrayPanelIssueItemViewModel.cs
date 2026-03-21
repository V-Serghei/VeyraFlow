namespace Veyra.Desktop.ViewModels.Windows;

public sealed class TrayPanelIssueItemViewModel
{
    public TrayPanelIssueItemViewModel(
        int repositoryId,
        string repositoryName,
        string summaryText,
        string glyph,
        string accentColor,
        string issueCategoryCode,
        bool canRetry,
        bool canCancel)
    {
        RepositoryId = repositoryId;
        RepositoryName = repositoryName;
        SummaryText = summaryText;
        Glyph = glyph;
        AccentColor = accentColor;
        IssueCategoryCode = issueCategoryCode;
        CanRetry = canRetry;
        CanCancel = canCancel;
    }

    public int RepositoryId { get; }
    public string RepositoryName { get; }
    public string SummaryText { get; }
    public string Glyph { get; }
    public string AccentColor { get; }
    public string IssueCategoryCode { get; }
    public bool CanRetry { get; }
    public bool CanCancel { get; }
}
