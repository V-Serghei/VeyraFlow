using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Commands.Repository;
using Veyra.Application.Queries;
using Veyra.Application.Queries.Repository;
using Veyra.Desktop.Services.Navigation;

namespace Veyra.Desktop.ViewModels.Pages.Dashboard;

public sealed partial class RepositoryDashboardViewModel : ObservableObject
{
    private readonly IMediator _mediator;
    private readonly ILogger<RepositoryDashboardViewModel> _log;
    private readonly IWindowService _windows;

    public ObservableCollection<RepositoryCardViewModel> Repositories { get; } = new();

    [ObservableProperty] private RepositoryCardViewModel? _selectedRepository;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;

    // Detail panel state
    [ObservableProperty] private bool _isDetailVisible;
    [ObservableProperty] private string _detailName = string.Empty;
    [ObservableProperty] private string? _detailDescription;
    [ObservableProperty] private string _detailPath = string.Empty;
    [ObservableProperty] private ObservableCollection<string> _detailFormats = new();
    [ObservableProperty] private ObservableCollection<string> _availableFormats = new();

    public RepositoryDashboardViewModel(
        IMediator mediator,
        ILogger<RepositoryDashboardViewModel> log,
        IWindowService windows)
    {
        _mediator = mediator;
        _log = log;
        _windows = windows;
    }

    /// <summary>Call this after the view is loaded to populate data.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            // Ensure repos exist for all directories
            await _mediator.Send(new EnsureRepositoriesCommand());

            var repos = await _mediator.Send(new GetAllRepositoriesQuery());
            Repositories.Clear();

            foreach (var r in repos)
            {
                var card = new RepositoryCardViewModel
                {
                    Id = r.Id,
                    Name = r.Name,
                    Description = r.Description,
                    DirectoryPath = r.DirectoryPath,
                };
                foreach (var f in r.LinkedFormats)
                    card.LinkedFormats.Add(f);
                card.RefreshFormatsDisplay();
                Repositories.Add(card);
            }

            // Load available formats for linking
            List<string> allExts = await _mediator.Send(new GetTrackedExtensionsQuery());
            AvailableFormats = new ObservableCollection<string>(allExts);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load repositories");
            ErrorMessage = "Не удалось загрузить репозитории.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void SelectRepository(RepositoryCardViewModel? repo)
    {
        SelectedRepository = repo;
        if (repo is null)
        {
            IsDetailVisible = false;
            return;
        }

        DetailName = repo.Name;
        DetailDescription = repo.Description;
        DetailPath = repo.DirectoryPath;
        DetailFormats = new ObservableCollection<string>(repo.LinkedFormats.ToList());
        IsDetailVisible = true;
    }

    [RelayCommand]
    private async Task AddRepositoryAsync()
    {
        Window? owner = _windows.GetActiveWindow();
        if (owner is null) return;

        var res = await owner.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Выберите папку для нового репозитория", AllowMultiple = false });

        var folder = res.FirstOrDefault();
        var local = folder?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(local) || !Directory.Exists(local)) return;

        try
        {
            var result = await _mediator.Send(new AddDirectoryAndCreateRepositoryCommand(local, null));
            if (result.Success)
                await LoadAsync();
            else
                ErrorMessage = result.Error;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to add repository");
            ErrorMessage = "Не удалось добавить репозиторий.";
        }
    }

    [RelayCommand]
    private async Task DeleteRepositoryAsync()
    {
        if (SelectedRepository is null) return;

        try
        {
            await _mediator.Send(new DeleteRepositoryCommand(SelectedRepository.Id));
            IsDetailVisible = false;
            SelectedRepository = null;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to delete repository");
            ErrorMessage = "Не удалось удалить репозиторий.";
        }
    }

    [RelayCommand]
    private async Task SaveDetailAsync()
    {
        if (SelectedRepository is null) return;

        try
        {
            await _mediator.Send(new UpdateRepositoryCommand(
                SelectedRepository.Id, DetailName, DetailDescription));
            await LoadAsync();

            // Re-select
            var updated = Repositories.FirstOrDefault(r => r.Id == SelectedRepository.Id);
            if (updated is not null)
                SelectRepository(updated);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to save repository details");
            ErrorMessage = "Не удалось сохранить изменения.";
        }
    }

    [RelayCommand]
    private async Task LinkFormatAsync(string format)
    {
        if (SelectedRepository is null || string.IsNullOrWhiteSpace(format)) return;

        try
        {
            await _mediator.Send(new LinkFormatsToRepositoryCommand(
                SelectedRepository.Id, new[] { format }));

            if (!DetailFormats.Contains(format))
                DetailFormats.Add(format);

            await LoadAsync();
            var updated = Repositories.FirstOrDefault(r => r.Id == SelectedRepository.Id);
            if (updated is not null)
                SelectRepository(updated);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to link format");
        }
    }

    [RelayCommand]
    private async Task UnlinkFormatAsync(string format)
    {
        if (SelectedRepository is null || string.IsNullOrWhiteSpace(format)) return;

        try
        {
            await _mediator.Send(new UnlinkFormatsFromRepositoryCommand(
                SelectedRepository.Id, new[] { format }));

            DetailFormats.Remove(format);

            await LoadAsync();
            var updated = Repositories.FirstOrDefault(r => r.Id == SelectedRepository.Id);
            if (updated is not null)
                SelectRepository(updated);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to unlink format");
        }
    }
}
