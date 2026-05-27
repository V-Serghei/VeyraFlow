namespace Veyra.Desktop.ViewModels.Pages.Dashboard;

internal readonly record struct SearchDirectives(
    string TextQuery,
    string AvailabilityFilter,
    string SyncStateFilter,
    string FormatTagFilter,
    double? MinSizeMb,
    double? MaxSizeMb)
{
    public static SearchDirectives Empty => new(
        TextQuery: string.Empty,
        AvailabilityFilter: string.Empty,
        SyncStateFilter: string.Empty,
        FormatTagFilter: string.Empty,
        MinSizeMb: null,
        MaxSizeMb: null);
}
