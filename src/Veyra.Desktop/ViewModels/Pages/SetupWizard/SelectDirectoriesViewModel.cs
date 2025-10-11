using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Platform.Storage;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Commands.Setup;
using Veyra.Desktop.Native;
using Veyra.Desktop.Services.Navigation;

namespace Veyra.Desktop.ViewModels.Pages.SetupWizard;

public sealed class SelectDirectoriesViewModel
{
    private readonly IMediator _mediator;
    private readonly ILogger<SelectDirectoriesViewModel> _log;
    private readonly IWindowService _windows;

    public ObservableCollection<string> Directories { get; } = new();

    public string? InputPath { get; set; }

    public ICommand BrowseCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }

    public SelectDirectoriesViewModel(IMediator mediator,
        ILogger<SelectDirectoriesViewModel> log,
        IWindowService windows)
    {
        _mediator = mediator;
        _log = log;
        _windows = windows;

        BrowseCommand = new RelayCommand(async void (_) =>
        {
            try
            {
                await BrowseAsync();
            }
            catch (Exception e)
            {
                throw; // TODO handle exception
            }
        });
        AddCommand    = new RelayCommand(_ => Add(), _ => !string.IsNullOrWhiteSpace(InputPath));
        RemoveCommand = new RelayCommand(p => Remove(p as string));
    }

    private async Task BrowseAsync()
    {
        var owner = _windows.GetActiveWindow();
        if (owner is null) return;

        var result = await owner.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { AllowMultiple = false, Title = "Выберите папку" });

        var folder = result?.FirstOrDefault();
        var localPath = folder?.Path?.LocalPath; // Uri → local path

        if (!string.IsNullOrWhiteSpace(localPath))
        {
            InputPath = localPath;
            Add();
        }
    }

    private void Add()
    {
        if (string.IsNullOrWhiteSpace(InputPath)) return;
        var p = InputPath.Trim();

        if (!Directory.Exists(p)) return;
        if (IsRootDrive(p)) return;
        if (Directories.Contains(p)) return;

        Directories.Add(p);
        InputPath = string.Empty;
    }

    private void Remove(string? path)
    {
        if (path is null) return;
        Directories.Remove(path);
    }

    private static bool IsRootDrive(string p)
        => Path.GetPathRoot(p)?.TrimEnd('\\')?.Equals(p.TrimEnd('\\'), System.StringComparison.OrdinalIgnoreCase) == true;

    public string FooterText => Directories.Count == 0 ? "Папки не выбраны" : $"{Directories.Count} выбрано";
    public bool HasAny => Directories.Count > 0;

    public async Task<bool> CommitAsync()
    {
        var cmd = new SetWatchedDirectoriesCommand(Directories.ToList());
        try { await _mediator.Send(cmd); return true; }
        catch (System.Exception ex) { _log.LogError(ex, "Failed to save watched directories."); return false; }
    }
}
