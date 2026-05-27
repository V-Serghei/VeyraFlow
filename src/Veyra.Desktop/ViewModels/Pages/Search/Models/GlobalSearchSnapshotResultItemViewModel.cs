namespace Veyra.Desktop.ViewModels.Pages.Search;

public sealed record GlobalSearchSnapshotResultItemViewModel(
    int RepositoryId,
    long SnapshotId,
    string RepositoryName,
    string Title,
    string TriggerText,
    string CreatedText,
    string ChangedFilesText,
    string TagsText,
    bool HasTitle,
    bool HasTags);
