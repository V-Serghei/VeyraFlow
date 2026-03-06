using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Commands.Repository;
using Veyra.Application.Queries;
using Veyra.Application.Queries.Repository;
using Veyra.Desktop.Services.Navigation;

namespace Veyra.Desktop.ViewModels.Pages.RepositorySettings;

public sealed partial class RepositorySettingsViewModel : ObservableObject
{
    private readonly IMediator _mediator;
    private readonly IWindowService _windows;
    private readonly ILogger<RepositorySettingsViewModel> _log;

    public event Action? BackRequested;
    public event Func<int, Task>? RepositoryUpdated;
    public event Action? RepositoryDeleted;

    [ObservableProperty] private int _repositoryId;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _repositoryName = string.Empty;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string _directoryPath = string.Empty;
    [ObservableProperty] private string _customFormat = string.Empty;

    public ObservableCollection<string> SelectedFormats { get; } = [];
    public ObservableCollection<string> AvailableFormats { get; } = [];

    public RepositorySettingsViewModel(
        IMediator mediator,
        IWindowService windows,
        ILogger<RepositorySettingsViewModel> log)
    {
        _mediator = mediator;
        _windows = windows;
        _log = log;
    }

    public async Task LoadAsync(int repositoryId)
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            var repo = await _mediator.Send(new GetRepositoryDetailQuery(repositoryId));
            if (repo is null)
            {
                ErrorMessage = "Репозиторий не найден.";
                return;
            }

            var allFormats = await _mediator.Send(new GetTrackedExtensionsQuery());

            RepositoryId = repo.Id;
            RepositoryName = repo.Name;
            Description = repo.Description;
            DirectoryPath = repo.DirectoryPath;

            SelectedFormats.Clear();
            foreach (var format in repo.LinkedFormats.OrderBy(x => x))
                SelectedFormats.Add(format);

            AvailableFormats.Clear();
            foreach (var format in allFormats.OrderBy(x => x))
                AvailableFormats.Add(format);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load repository settings for {RepositoryId}", repositoryId);
            ErrorMessage = "Не удалось загрузить настройки репозитория.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task BrowseDirectoryAsync()
    {
        Window? owner = _windows.GetActiveWindow();
        if (owner is null)
            return;

        var res = await owner.StorageProvider.OpenFolderPickerAsync(
            new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "Выберите директорию репозитория",
                AllowMultiple = false
            });

        var local = res.FirstOrDefault()?.Path.LocalPath;
        if (!string.IsNullOrWhiteSpace(local) && Directory.Exists(local))
            DirectoryPath = local;
    }

    [RelayCommand]
    private void AddAvailableFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return;

        var normalized = NormalizeFormat(format);
        if (SelectedFormats.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            return;

        SelectedFormats.Add(normalized);
    }

    [RelayCommand]
    private void AddCustomFormat()
    {
        var normalized = NormalizeFormat(CustomFormat);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        if (!AvailableFormats.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            AvailableFormats.Add(normalized);

        if (!SelectedFormats.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            SelectedFormats.Add(normalized);

        CustomFormat = string.Empty;
    }

    [RelayCommand]
    private void RemoveFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return;

        var match = SelectedFormats.FirstOrDefault(f =>
            f.Equals(format, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
            SelectedFormats.Remove(match);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            var result = await _mediator.Send(new UpdateRepositoryConfigurationCommand(
                RepositoryId,
                RepositoryName,
                Description,
                DirectoryPath,
                SelectedFormats.ToList()));

            if (!result.Success)
            {
                ErrorMessage = result.Error ?? "Не удалось сохранить настройки репозитория.";
                return;
            }

            if (RepositoryUpdated is not null)
                await RepositoryUpdated.Invoke(RepositoryId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to save repository settings for {RepositoryId}", RepositoryId);
            ErrorMessage = "Не удалось сохранить изменения.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            await _mediator.Send(new DeleteRepositoryCommand(RepositoryId));
            RepositoryDeleted?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to delete repository {RepositoryId}", RepositoryId);
            ErrorMessage = "Не удалось удалить репозиторий.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Back()
    {
        BackRequested?.Invoke();
    }

    private static string NormalizeFormat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var v = value.Trim();
        if (!v.StartsWith('.'))
            v = "." + v;

        return v.ToLowerInvariant();
    }
}
