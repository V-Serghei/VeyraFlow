using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Navigation;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class InfoWindowViewModel : ObservableObject
{
    private const string RetentionSettingsLink = "settings.storage.retention";
    private const string CloudEncryptionSettingsLink = "settings.cloud.encryption";
    private readonly IWindowService? _windows;
    private readonly LocalizationManager _localization = LocalizationManager.Instance;
    private IReadOnlyList<HelpArticleDefinition> _definitions = Array.Empty<HelpArticleDefinition>();

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private HelpModeFilterViewModel? _selectedModeFilter;

    [ObservableProperty]
    private HelpArticleViewModel? _selectedArticle;

    public InfoWindowViewModel()
        : this(null)
    {
    }

    public InfoWindowViewModel(IWindowService? windows)
    {
        _windows = windows;

        ModeFilters =
        [
            new("office", "help.mode_office"),
            new("professional", "help.mode_professional")
        ];

        SelectedModeFilter = ModeFilters.FirstOrDefault();
        _localization.LanguageChanged += OnLanguageChanged;
        RebuildArticles(preserveSelection: false);
    }

    public event Func<string, Task>? DeepLinkRequested;

    public ObservableCollection<HelpModeFilterViewModel> ModeFilters { get; }
    public ObservableCollection<HelpArticleViewModel> VisibleArticles { get; } = [];

    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);
    public bool HasVisibleArticles => VisibleArticles.Count > 0;
    public string SelectedModeTitle => SelectedModeFilter?.Title ?? string.Empty;

    [RelayCommand]
    private void Close() => _windows?.GetActiveWindow()?.Close();

    [RelayCommand]
    private void ClearSearch()
    {
        SearchQuery = string.Empty;
    }

    [RelayCommand]
    private void OpenArticle(HelpArticleViewModel? article)
    {
        if (article is not null)
            SelectedArticle = article;
    }

    [RelayCommand]
    private async Task OpenDeepLinkAsync(string? link)
    {
        if (string.IsNullOrWhiteSpace(link) || DeepLinkRequested is null)
            return;

        await DeepLinkRequested.Invoke(link);
        _windows?.GetActiveWindow()?.Close();
    }

    partial void OnSearchQueryChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchQuery));
        RefreshVisibleArticles();
    }

    partial void OnSelectedModeFilterChanged(HelpModeFilterViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedModeTitle));
        RefreshVisibleArticles();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        var selectedKey = SelectedArticle?.Key;

        foreach (var mode in ModeFilters)
            mode.RefreshTitle();

        RebuildArticles(preserveSelection: true, selectedKey);
        OnPropertyChanged(nameof(SelectedModeTitle));
    }

    private void RebuildArticles(bool preserveSelection, string? selectedKey = null)
    {
        _definitions = BuildDefinitions();
        RefreshVisibleArticles();

        if (preserveSelection && !string.IsNullOrWhiteSpace(selectedKey))
        {
            SelectedArticle = VisibleArticles.FirstOrDefault(article =>
                string.Equals(article.Key, selectedKey, StringComparison.OrdinalIgnoreCase));
        }

        SelectedArticle ??= VisibleArticles.FirstOrDefault();
    }

    private void RefreshVisibleArticles()
    {
        var mode = SelectedModeFilter?.Key ?? "office";
        var query = SearchQuery.Trim();
        var previousKey = SelectedArticle?.Key;

        VisibleArticles.Clear();

        foreach (var definition in _definitions)
        {
            if (!string.Equals(definition.Mode, mode, StringComparison.OrdinalIgnoreCase))
                continue;

            var article = ToArticle(definition);
            if (!MatchesSearch(article, query))
                continue;

            VisibleArticles.Add(article);
        }

        OnPropertyChanged(nameof(HasVisibleArticles));

        SelectedArticle = VisibleArticles.FirstOrDefault(article =>
                              string.Equals(article.Key, previousKey, StringComparison.OrdinalIgnoreCase))
                          ?? VisibleArticles.FirstOrDefault();
    }

    private static bool MatchesSearch(HelpArticleViewModel article, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        return article.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static HelpArticleViewModel ToArticle(HelpArticleDefinition definition)
    {
        var blocks = definition.Blocks
            .Select(ToBlock)
            .ToList();

        var title = Loc.T(definition.TitleKey);
        var category = Loc.T(definition.CategoryKey);
        var summary = Loc.T(definition.SummaryKey);
        var searchText = string.Join(
            " ",
            title,
            category,
            summary,
            string.Join(" ", blocks.Select(block => block.SearchText)));

        return new HelpArticleViewModel(
            definition.Key,
            definition.Mode,
            category,
            title,
            summary,
            blocks,
            searchText);
    }

    private static HelpBlockViewModel ToBlock(HelpBlockDefinition block)
        => block.Kind switch
        {
            HelpBlockKind.Bullet => HelpBlockViewModel.Bullet(Loc.T(block.TextKey)),
            HelpBlockKind.Path => HelpBlockViewModel.Path(Loc.T(block.TextKey)),
            HelpBlockKind.Action => HelpBlockViewModel.Action(
                Loc.T(block.TextKey),
                Loc.T(block.ActionLabelKey ?? "help.open_settings_link"),
                block.ActionLink),
            HelpBlockKind.Visual => HelpBlockViewModel.Visual(Loc.T(block.TextKey)),
            HelpBlockKind.Callout => HelpBlockViewModel.Callout(Loc.T(block.TextKey)),
            _ => HelpBlockViewModel.Paragraph(Loc.T(block.TextKey))
        };

    private static IReadOnlyList<HelpArticleDefinition> BuildDefinitions()
        =>
        [
            new(
                "office_versions",
                "office",
                "help.category_storage",
                "help.office_versions_title",
                "help.office_versions_summary",
                [
                    HelpBlockDefinition.Paragraph("help.office_versions_p1"),
                    HelpBlockDefinition.Bullet("help.office_versions_b1"),
                    HelpBlockDefinition.Bullet("help.office_versions_b2"),
                    HelpBlockDefinition.Bullet("help.office_versions_b3"),
                    HelpBlockDefinition.Bullet("help.office_versions_b4"),
                    HelpBlockDefinition.Path("help.path_retention"),
                    HelpBlockDefinition.Action("help.open_retention_settings", RetentionSettingsLink),
                    HelpBlockDefinition.Visual("help.office_versions_visual")
                ]),
            new(
                "office_cleanup",
                "office",
                "help.category_storage",
                "help.office_cleanup_title",
                "help.office_cleanup_summary",
                [
                    HelpBlockDefinition.Paragraph("help.office_cleanup_p1"),
                    HelpBlockDefinition.Bullet("help.office_cleanup_b1"),
                    HelpBlockDefinition.Bullet("help.office_cleanup_b2"),
                    HelpBlockDefinition.Bullet("help.office_cleanup_b3"),
                    HelpBlockDefinition.Callout("help.office_cleanup_callout"),
                    HelpBlockDefinition.Path("help.path_retention")
                ]),
            new(
                "office_cloud_history",
                "office",
                "help.category_cloud",
                "help.office_cloud_history_title",
                "help.office_cloud_history_summary",
                [
                    HelpBlockDefinition.Paragraph("help.office_cloud_history_p1"),
                    HelpBlockDefinition.Bullet("help.office_cloud_history_b1"),
                    HelpBlockDefinition.Bullet("help.office_cloud_history_b2"),
                    HelpBlockDefinition.Bullet("help.office_cloud_history_b3"),
                    HelpBlockDefinition.Path("help.path_retention"),
                    HelpBlockDefinition.Action("help.open_retention_settings", RetentionSettingsLink),
                    HelpBlockDefinition.Callout("help.office_cloud_history_callout")
                ]),
            new(
                "office_restore",
                "office",
                "help.category_recovery",
                "help.office_restore_title",
                "help.office_restore_summary",
                [
                    HelpBlockDefinition.Paragraph("help.office_restore_p1"),
                    HelpBlockDefinition.Bullet("help.office_restore_b1"),
                    HelpBlockDefinition.Bullet("help.office_restore_b2"),
                    HelpBlockDefinition.Bullet("help.office_restore_b3"),
                    HelpBlockDefinition.Visual("help.office_restore_visual")
                ]),
            new(
                "professional_retention",
                "professional",
                "help.category_storage",
                "help.pro_retention_title",
                "help.pro_retention_summary",
                [
                    HelpBlockDefinition.Paragraph("help.pro_retention_p1"),
                    HelpBlockDefinition.Bullet("help.pro_retention_b1"),
                    HelpBlockDefinition.Bullet("help.pro_retention_b2"),
                    HelpBlockDefinition.Bullet("help.pro_retention_b3"),
                    HelpBlockDefinition.Bullet("help.pro_retention_b4"),
                    HelpBlockDefinition.Path("help.path_retention"),
                    HelpBlockDefinition.Action("help.open_retention_settings", RetentionSettingsLink)
                ]),
            new(
                "professional_hierarchy",
                "professional",
                "help.category_storage",
                "help.pro_hierarchy_title",
                "help.pro_hierarchy_summary",
                [
                    HelpBlockDefinition.Paragraph("help.pro_hierarchy_p1"),
                    HelpBlockDefinition.Bullet("help.pro_hierarchy_b1"),
                    HelpBlockDefinition.Bullet("help.pro_hierarchy_b2"),
                    HelpBlockDefinition.Bullet("help.pro_hierarchy_b3"),
                    HelpBlockDefinition.Visual("help.pro_hierarchy_visual"),
                    HelpBlockDefinition.Path("help.path_retention")
                ]),
            new(
                "professional_cloud",
                "professional",
                "help.category_cloud",
                "help.pro_cloud_title",
                "help.pro_cloud_summary",
                [
                    HelpBlockDefinition.Paragraph("help.pro_cloud_p1"),
                    HelpBlockDefinition.Bullet("help.pro_cloud_b1"),
                    HelpBlockDefinition.Bullet("help.pro_cloud_b2"),
                    HelpBlockDefinition.Bullet("help.pro_cloud_b3"),
                    HelpBlockDefinition.Path("help.path_cloud_encryption"),
                    HelpBlockDefinition.Action("help.open_cloud_settings", CloudEncryptionSettingsLink)
                ]),
            new(
                "professional_blocks",
                "professional",
                "help.category_storage",
                "help.pro_blocks_title",
                "help.pro_blocks_summary",
                [
                    HelpBlockDefinition.Paragraph("help.pro_blocks_p1"),
                    HelpBlockDefinition.Bullet("help.pro_blocks_b1"),
                    HelpBlockDefinition.Bullet("help.pro_blocks_b2"),
                    HelpBlockDefinition.Bullet("help.pro_blocks_b3"),
                    HelpBlockDefinition.Callout("help.pro_blocks_callout")
                ]),
            new(
                "professional_help_architecture",
                "professional",
                "help.category_help",
                "help.pro_help_arch_title",
                "help.pro_help_arch_summary",
                [
                    HelpBlockDefinition.Paragraph("help.pro_help_arch_p1"),
                    HelpBlockDefinition.Bullet("help.pro_help_arch_b1"),
                    HelpBlockDefinition.Bullet("help.pro_help_arch_b2"),
                    HelpBlockDefinition.Bullet("help.pro_help_arch_b3")
                ])
        ];
}

public sealed partial class HelpModeFilterViewModel(string key, string titleKey) : ObservableObject
{
    public string Key { get; } = key;
    public string TitleKey { get; } = titleKey;

    [ObservableProperty]
    private string _title = Loc.T(titleKey);

    public void RefreshTitle() => Title = Loc.T(TitleKey);
}

public sealed record HelpArticleViewModel(
    string Key,
    string Mode,
    string Category,
    string Title,
    string Summary,
    IReadOnlyList<HelpBlockViewModel> Blocks,
    string SearchText);

public sealed record HelpBlockViewModel(
    HelpBlockKind Kind,
    string Text,
    string? ActionLabel,
    string? ActionLink)
{
    public bool IsParagraph => Kind == HelpBlockKind.Paragraph;
    public bool IsBullet => Kind == HelpBlockKind.Bullet;
    public bool IsPath => Kind == HelpBlockKind.Path;
    public bool IsAction => Kind == HelpBlockKind.Action;
    public bool IsVisual => Kind == HelpBlockKind.Visual;
    public bool IsCallout => Kind == HelpBlockKind.Callout;
    public bool HasAction => !string.IsNullOrWhiteSpace(ActionLink);
    public string SearchText => string.Join(" ", Text, ActionLabel);

    public static HelpBlockViewModel Paragraph(string text) => new(HelpBlockKind.Paragraph, text, null, null);
    public static HelpBlockViewModel Bullet(string text) => new(HelpBlockKind.Bullet, text, null, null);
    public static HelpBlockViewModel Path(string text) => new(HelpBlockKind.Path, text, null, null);
    public static HelpBlockViewModel Callout(string text) => new(HelpBlockKind.Callout, text, null, null);
    public static HelpBlockViewModel Visual(string text) => new(HelpBlockKind.Visual, text, null, null);
    public static HelpBlockViewModel Action(string text, string actionLabel, string? actionLink)
        => new(HelpBlockKind.Action, text, actionLabel, actionLink);
}

public enum HelpBlockKind
{
    Paragraph,
    Bullet,
    Path,
    Action,
    Visual,
    Callout
}

internal sealed record HelpArticleDefinition(
    string Key,
    string Mode,
    string CategoryKey,
    string TitleKey,
    string SummaryKey,
    IReadOnlyList<HelpBlockDefinition> Blocks);

internal sealed record HelpBlockDefinition(
    HelpBlockKind Kind,
    string TextKey,
    string? ActionLink = null,
    string? ActionLabelKey = null)
{
    public static HelpBlockDefinition Paragraph(string textKey) => new(HelpBlockKind.Paragraph, textKey);
    public static HelpBlockDefinition Bullet(string textKey) => new(HelpBlockKind.Bullet, textKey);
    public static HelpBlockDefinition Path(string textKey) => new(HelpBlockKind.Path, textKey);
    public static HelpBlockDefinition Callout(string textKey) => new(HelpBlockKind.Callout, textKey);
    public static HelpBlockDefinition Visual(string textKey) => new(HelpBlockKind.Visual, textKey);
    public static HelpBlockDefinition Action(string textKey, string actionLink, string? actionLabelKey = null)
        => new(HelpBlockKind.Action, textKey, actionLink, actionLabelKey);
}
