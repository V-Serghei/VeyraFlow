using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Observability;
using Veyra.Application.DTOs;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed record OperationJournalFilterOptionItem(string Value, string Label);

public sealed record OperationJournalWindowItemViewModel(
    long Id,
    DateTime OccurredAtLocal,
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

public sealed partial class OperationJournalWindowViewModel : ObservableObject
{
    private readonly IOperationJournalService _journal;
    private readonly ILogger<OperationJournalWindowViewModel> _log;
    private readonly List<OperationJournalWindowItemViewModel> _allItems = [];

    public event Action? RequestClose;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _dateFilter = string.Empty;
    [ObservableProperty] private OperationJournalFilterOptionItem? _selectedLevelFilter;
    [ObservableProperty] private OperationJournalFilterOptionItem? _selectedCategoryFilter;
    [ObservableProperty] private OperationJournalWindowItemViewModel? _selectedItem;

    public ObservableCollection<OperationJournalWindowItemViewModel> Items { get; } = [];
    public ObservableCollection<OperationJournalFilterOptionItem> LevelFilters { get; } = [];
    public ObservableCollection<OperationJournalFilterOptionItem> CategoryFilters { get; } = [];

    public bool HasSelectedItem => SelectedItem is not null;
    public bool HasNoItems => !IsLoading && Items.Count == 0;

    public OperationJournalWindowViewModel(
        IOperationJournalService journal,
        ILogger<OperationJournalWindowViewModel> log)
    {
        _journal = journal;
        _log = log;
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilters();
    partial void OnDateFilterChanged(string value) => ApplyFilters();

    partial void OnSelectedLevelFilterChanged(OperationJournalFilterOptionItem? value)
        => ApplyFilters();

    partial void OnSelectedCategoryFilterChanged(OperationJournalFilterOptionItem? value)
        => ApplyFilters();

    partial void OnSelectedItemChanged(OperationJournalWindowItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedItem));
    }

    public async Task LoadAsync()
    {
        try
        {
            IsLoading = true;
            var entries = await _journal.GetRecentAsync(500);
            _allItems.Clear();
            _allItems.AddRange(entries.Select(MapEntry));
            RebuildFilterOptions();
            ApplyFilters();
            SelectedItem = Items.FirstOrDefault();
            _log.LogInformation("Operation journal dialog loaded. Items {Count}", _allItems.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load operation journal window");
            _allItems.Clear();
            Items.Clear();
            SelectedItem = null;
            RebuildFilterOptions();
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasNoItems));
        }
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    [RelayCommand]
    private void ClearFilters()
    {
        SearchQuery = string.Empty;
        DateFilter = string.Empty;
        SelectedLevelFilter = LevelFilters.FirstOrDefault();
        SelectedCategoryFilter = CategoryFilters.FirstOrDefault();
        ApplyFilters();
    }

    private void RebuildFilterOptions()
    {
        LevelFilters.Clear();
        LevelFilters.Add(new OperationJournalFilterOptionItem("all", Loc.T("filter.option.all")));
        foreach (var level in _allItems.Select(x => x.Level).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            LevelFilters.Add(new OperationJournalFilterOptionItem(level, Humanize(level)));

        CategoryFilters.Clear();
        CategoryFilters.Add(new OperationJournalFilterOptionItem("all", Loc.T("filter.option.all")));
        foreach (var category in _allItems.Select(x => x.Category).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            CategoryFilters.Add(new OperationJournalFilterOptionItem(category, Humanize(category)));

        SelectedLevelFilter ??= LevelFilters.FirstOrDefault();
        SelectedCategoryFilter ??= CategoryFilters.FirstOrDefault();
    }

    private void ApplyFilters()
    {
        var level = SelectedLevelFilter?.Value ?? "all";
        var category = SelectedCategoryFilter?.Value ?? "all";
        var search = SearchQuery.Trim();
        var date = DateFilter.Trim();

        var filtered = _allItems.Where(item =>
        {
            if (!string.Equals(level, "all", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(item.Level, level, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.Equals(category, "all", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(date) &&
                !item.TimestampText.Contains(date, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(search) &&
                !item.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }).ToList();

        Items.Clear();
        foreach (var item in filtered)
            Items.Add(item);

        if (SelectedItem is null || !Items.Contains(SelectedItem))
            SelectedItem = Items.FirstOrDefault();

        OnPropertyChanged(nameof(HasNoItems));
    }

    private static OperationJournalWindowItemViewModel MapEntry(OperationJournalEntryDto entry)
    {
        var occurredLocal = entry.OccurredAtUtc.ToLocalTime();
        var scope = entry.RepositoryId.HasValue
            ? $"repo:{entry.RepositoryId.Value}"
            : string.IsNullOrWhiteSpace(entry.Username)
                ? "-"
                : entry.Username!;

        var details = string.IsNullOrWhiteSpace(entry.Details) ? "-" : entry.Details!;
        var timestamp = occurredLocal.ToString("yyyy-MM-dd HH:mm:ss");
        var levelLabel = Humanize(entry.Level);
        var categoryLabel = Humanize(entry.Category);
        var searchText = string.Join(' ',
            timestamp,
            entry.Level,
            entry.Category,
            entry.Action,
            scope,
            entry.Message,
            details);

        return new OperationJournalWindowItemViewModel(
            entry.Id,
            occurredLocal,
            timestamp,
            entry.Level,
            levelLabel,
            entry.Category,
            categoryLabel,
            entry.Action,
            scope,
            entry.Message,
            details,
            searchText);
    }

    private static string Humanize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "-";

        var text = value.Replace('_', ' ').Trim();
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
