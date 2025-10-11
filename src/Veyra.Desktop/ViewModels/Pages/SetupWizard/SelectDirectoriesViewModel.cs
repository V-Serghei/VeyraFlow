using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Commands.Setup;
using Veyra.Desktop.Native;
using Veyra.Desktop.Services.Navigation;

namespace Veyra.Desktop.ViewModels.Pages.SetupWizard;

public sealed class SelectDirectoriesViewModel : INotifyPropertyChanged
{
    // Injected services
    private readonly IMediator _mediator;
    private readonly ILogger<SelectDirectoriesViewModel> _log;
    private readonly IWindowService _windows;

    // INotifyPropertyChanged implementation
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    public event EventHandler? SelectionChanged;
    // Properties and Commands
    public ObservableCollection<string> Directories { get; } = new();
    public ICommand BrowseCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
    // Computed properties
    public string FooterText => Directories.Count == 0 ? "Folders not selected" : $"{Directories.Count} selected folder(s)";
    public bool HasAny => Directories.Count > 0;
    public string? InputPath { get; set; }

    /// <summary>
    /// Constructor for SelectDirectoriesViewModel.
    /// This view model allows users to select and manage a list of directories.
    /// </summary>
    /// <param name="mediator"></param>
    /// <param name="log"></param>
    /// <param name="windows"></param>
    public SelectDirectoriesViewModel(IMediator mediator, ILogger<SelectDirectoriesViewModel> log, IWindowService windows)
    {
        _mediator = mediator; _log = log; _windows = windows;

        Directories.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(FooterText));
            OnPropertyChanged(nameof(HasAny));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        };

        BrowseCommand = new RelayCommand(async _ => await BrowseAsync());
        AddCommand    = new RelayCommand(_ => Add(), _ => !string.IsNullOrWhiteSpace(InputPath));
        RemoveCommand = new RelayCommand(p => Remove(p as string));
    }

    /// <summary>
    /// Opens a folder picker dialog to allow the user to select a directory.
    /// The selected directory is added to the Directories collection if valid.
    /// </summary>
    private async Task BrowseAsync()
    {
        Window? owner = _windows.GetActiveWindow();
        if (owner is null) return;

        IReadOnlyList<IStorageFolder> res = await owner.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Select a folder", AllowMultiple = false });

        IStorageFolder? folder = res.FirstOrDefault();
        string? local  = folder?.Path.LocalPath;

        if (!string.IsNullOrWhiteSpace(local))
        {
            InputPath = local;
            Add();
        }
    }

    /// <summary>
    /// Adds the directory specified in InputPath to the Directories collection if it is valid.
    /// Validity checks include ensuring the path is not empty, exists, is not a root drive,
    /// and is not already in the collection.
    /// </summary>
    private void Add()
    {
        if (string.IsNullOrWhiteSpace(InputPath)) return;
        string p = InputPath.Trim();
        if (!Directory.Exists(p)) return;
        if (IsRootDrive(p)) return;
        if (Directories.Contains(p)) return;

        Directories.Add(p);
        InputPath = string.Empty;
        OnPropertyChanged(nameof(InputPath));
    }

    /// <summary>
    /// Removes the specified path from the Directories collection.
    /// If the path is null, the method does nothing.
    /// </summary>
    /// <param name="path"></param>
    private void Remove(string? path)
    {
        if (path is null) return;
        Directories.Remove(path);
    }

    /// <summary>
    /// Determines if the given path is a root drive (e.g., "C:\").
    /// This is used to prevent users from selecting root drives as watched directories.
    /// </summary>
    /// <param name="p"></param>
    /// <returns></returns>
    private static bool IsRootDrive(string p)
        => Path.GetPathRoot(p)?.TrimEnd('\\').Equals(p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Commits the selected directories by sending a SetWatchedDirectoriesCommand via MediatR.
    /// Returns true if the operation is successful, false otherwise.
    /// </summary>
    /// <returns></returns>
    public async Task<bool> CommitAsync()
    {
        try
        {
            await _mediator.Send(new SetWatchedDirectoriesCommand(Directories.ToList()));
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to save watched directories.");
            return false;
        }
    }
}
