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
using Microsoft.Extensions.Logging;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Native;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Services.Storage;

namespace Veyra.Desktop.ViewModels.Pages.SetupWizard;

public sealed class SelectDirectoriesViewModel : INotifyPropertyChanged
{
    private readonly ILogger<SelectDirectoriesViewModel> _log;
    private readonly IWindowService _windows;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    public event EventHandler? SelectionChanged;

    public ObservableCollection<string> Directories { get; } = new();
    public ICommand BrowseCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }

    public string FooterText => Directories.Count == 0
        ? Loc.T("setup.directories_none_selected")
        : Loc.P("setup.directories_selected", Directories.Count, Directories.Count);

    public bool HasAny => Directories.Count > 0;
    public string? InputPath { get; set; }

    public SelectDirectoriesViewModel(ILogger<SelectDirectoriesViewModel> log, IWindowService windows)
    {
        _log = log;
        _windows = windows;

        Directories.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(FooterText));
            OnPropertyChanged(nameof(HasAny));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        };

        LocalizationManager.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(FooterText));
        };

        BrowseCommand = new RelayCommand(async _ => await BrowseAsync());
        AddCommand = new RelayCommand(_ => Add(), _ => !string.IsNullOrWhiteSpace(InputPath));
        RemoveCommand = new RelayCommand(p => Remove(p as string));
    }

    private async Task BrowseAsync()
    {
        Window? owner = _windows.GetActiveWindow();
        if (owner is null) return;

        IReadOnlyList<IStorageFolder> res = await owner.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = Loc.T("setup.select_folder_title"), AllowMultiple = false });

        IStorageFolder? folder = res.FirstOrDefault();
        string? local = StoragePathResolver.TryGetLocalPath(folder);

        if (!string.IsNullOrWhiteSpace(local))
        {
            InputPath = local;
            Add();
        }
    }

    private void Add()
    {
        if (string.IsNullOrWhiteSpace(InputPath)) return;
        string p = InputPath.Trim();
        if (!Directory.Exists(p)) return;
        if (IsRootDrive(p)) return;
        if (Directories.Contains(p, StringComparer.OrdinalIgnoreCase)) return;

        Directories.Add(p);
        InputPath = string.Empty;
        OnPropertyChanged(nameof(InputPath));
    }

    private void Remove(string? path)
    {
        if (path is null) return;
        Directories.Remove(path);
    }

    private static bool IsRootDrive(string p)
        => Path.GetPathRoot(p)?.TrimEnd('\\').Equals(p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true;

    public Task<bool> CommitAsync()
    {
        if (Directories.Count == 0)
        {
            _log.LogWarning("No directories selected");
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    public IReadOnlyCollection<string> GetSelectedDirectories() => Directories.ToList();
}
