namespace Veyra.Desktop.ViewModels.Pages.Explorer;

internal readonly record struct SearchSnapshotFileDirectives(
    string TextQuery,
    string ChangeKindFilter,
    string ExtensionFilter,
    double? MinSizeDeltaKb)
{
    public static SearchSnapshotFileDirectives Empty => new(
        TextQuery: string.Empty,
        ChangeKindFilter: string.Empty,
        ExtensionFilter: string.Empty,
        MinSizeDeltaKb: null);
}
