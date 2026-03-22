using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class RepositoryFolderPickerWindowViewModel : ObservableObject
{
    private readonly LocalizationManager _localization = LocalizationManager.Instance;
    private IReadOnlyList<string> _allFolders = [];
    private string? _preferredSelection;

    public RepositoryFolderPickerWindowViewModel()
    {
        _localization.LanguageChanged += (_, _) => RefreshLocalizedState();
    }

    public event Action? RequestClose;

    public ObservableCollection<string> VisibleFolders { get; } = [];

    [ObservableProperty] private string _repositoryRoot = string.Empty;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string? _selectedFolder;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string? _errorMessage;

    public string? DialogResultRelativePath { get; private set; }
    public bool HasFolders => VisibleFolders.Count > 0;
    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool ShowEmptyState => !IsLoading && !HasErrorMessage && VisibleFolders.Count == 0;
    public bool CanConfirm => !IsLoading && !string.IsNullOrWhiteSpace(SelectedFolder);

    public async Task ConfigureAsync(string repositoryRoot, string? initialSelection = null)
    {
        RepositoryRoot = repositoryRoot;
        SearchText = string.Empty;
        ErrorMessage = null;
        StatusMessage = Loc.T("repo_settings.excluded_paths_picker_loading_detail");
        DialogResultRelativePath = null;
        _preferredSelection = NormalizeRelativePath(initialSelection);
        _allFolders = [];
        VisibleFolders.Clear();
        SelectedFolder = null;
        IsLoading = true;

        try
        {
            var folders = await Task.Run(() => EnumerateRepositoryFolders(repositoryRoot));
            _allFolders = folders;
            RefreshVisibleFolders();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = Loc.T("repo_settings.excluded_paths_picker_error");
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(CanConfirm));
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    [RelayCommand]
    private void Confirm()
    {
        if (string.IsNullOrWhiteSpace(SelectedFolder))
            return;

        DialogResultRelativePath = SelectedFolder;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogResultRelativePath = null;
        RequestClose?.Invoke();
    }

    partial void OnSearchTextChanged(string value)
    {
        RefreshVisibleFolders();
    }

    partial void OnSelectedFolderChanged(string? value)
    {
        OnPropertyChanged(nameof(CanConfirm));
    }

    private void RefreshVisibleFolders()
    {
        var search = (SearchText ?? string.Empty).Trim();
        var filtered = string.IsNullOrWhiteSpace(search)
            ? _allFolders
            : _allFolders.Where(path => path.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();

        VisibleFolders.Clear();
        foreach (var folder in filtered)
            VisibleFolders.Add(folder);

        if (!string.IsNullOrWhiteSpace(_preferredSelection)
            && VisibleFolders.Contains(_preferredSelection, StringComparer.OrdinalIgnoreCase))
        {
            SelectedFolder = VisibleFolders.First(item =>
                item.Equals(_preferredSelection, StringComparison.OrdinalIgnoreCase));
            _preferredSelection = null;
        }
        else if (!string.IsNullOrWhiteSpace(SelectedFolder)
                 && VisibleFolders.Contains(SelectedFolder, StringComparer.OrdinalIgnoreCase))
        {
            SelectedFolder = VisibleFolders.First(item =>
                item.Equals(SelectedFolder, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            SelectedFolder = VisibleFolders.FirstOrDefault();
        }

        StatusMessage = VisibleFolders.Count > 0
            ? Loc.F("repo_settings.excluded_paths_picker_count", VisibleFolders.Count)
            : Loc.T("repo_settings.excluded_paths_picker_empty");

        OnPropertyChanged(nameof(HasFolders));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void RefreshLocalizedState()
    {
        StatusMessage = IsLoading
            ? Loc.T("repo_settings.excluded_paths_picker_loading_detail")
            : VisibleFolders.Count > 0
                ? Loc.F("repo_settings.excluded_paths_picker_count", VisibleFolders.Count)
                : HasErrorMessage
                    ? Loc.T("repo_settings.excluded_paths_picker_error")
                    : Loc.T("repo_settings.excluded_paths_picker_empty");

        OnPropertyChanged(nameof(HasFolders));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private static IReadOnlyList<string> EnumerateRepositoryFolders(string repositoryRoot)
    {
        var normalizedRoot = Path.GetFullPath(repositoryRoot.Trim());
        if (!Directory.Exists(normalizedRoot))
            return [];

        var result = new List<string>(256);
        var stack = new Stack<string>();
        stack.Push(normalizedRoot);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            IReadOnlyList<string> children;
            try
            {
                children = Directory.EnumerateDirectories(current)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                continue;
            }

            for (var i = children.Count - 1; i >= 0; i--)
                stack.Push(children[i]);

            foreach (var child in children)
            {
                var relative = NormalizeRelativePath(Path.GetRelativePath(normalizedRoot, child));
                if (!string.IsNullOrWhiteSpace(relative))
                    result.Add(relative);
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static string? NormalizeRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value
            .Replace('\\', '/')
            .Trim()
            .Trim('/');

        return string.IsNullOrWhiteSpace(normalized) || normalized == "."
            ? null
            : normalized;
    }
}
