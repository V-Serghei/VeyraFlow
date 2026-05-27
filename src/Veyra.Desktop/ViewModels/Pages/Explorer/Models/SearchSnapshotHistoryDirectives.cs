namespace Veyra.Desktop.ViewModels.Pages.Explorer;

internal readonly record struct SearchSnapshotHistoryDirectives(
    string TextQuery,
    string KindFilter,
    string TriggerFilter,
    string TagFilter,
    int? MinChangedFiles)
{
    public static SearchSnapshotHistoryDirectives Empty => new(
        TextQuery: string.Empty,
        KindFilter: string.Empty,
        TriggerFilter: string.Empty,
        TagFilter: string.Empty,
        MinChangedFiles: null);
}
