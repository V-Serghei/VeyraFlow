namespace Veyra.Desktop.ViewModels.Pages.Explorer;

internal readonly record struct SearchDirectives(
    string TextQuery,
    string EntryTypeFilter,
    string ExtensionFilter,
    string ModifiedWindowFilter,
    double? MinSizeMb,
    double? MaxSizeMb)
{
    public static SearchDirectives Empty => new(
        TextQuery: string.Empty,
        EntryTypeFilter: string.Empty,
        ExtensionFilter: string.Empty,
        ModifiedWindowFilter: string.Empty,
        MinSizeMb: null,
        MaxSizeMb: null);
}
