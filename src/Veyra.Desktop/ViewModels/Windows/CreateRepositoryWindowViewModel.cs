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
using Veyra.Application.DTOs;
using Veyra.Desktop.Services.Navigation;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class RepositoryFormatOptionViewModel : ObservableObject
{
    public RepositoryFormatOptionViewModel(string format, bool isSelected = true)
    {
        Format = NormalizeFormat(format);
        _isSelected = isSelected;
    }

    public string Format { get; }

    [ObservableProperty] private bool _isSelected;

    public static string NormalizeFormat(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var v = value.Trim();
        if (!v.StartsWith('.'))
            v = "." + v;

        return v.ToLowerInvariant();
    }
}

public sealed partial class CreateRepositoryWindowViewModel : ObservableObject
{
    private readonly IMediator _mediator;
    private readonly IWindowService _windows;
    private readonly ILogger<CreateRepositoryWindowViewModel> _log;

    private int _stepIndex;

    public event Action? RequestClose;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isCompleted;
    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty] private string _repositoryName = string.Empty;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string _directoryPath = string.Empty;
    [ObservableProperty] private string _customFormat = string.Empty;

    [ObservableProperty] private int _progressPercent;
    [ObservableProperty] private string _progressMessage = "Ожидание запуска";
    [ObservableProperty] private int _filesProcessed;
    [ObservableProperty] private int _filesTotal;

    public ObservableCollection<RepositoryFormatOptionViewModel> Formats { get; } = [];

    public IRelayCommand BackCommand { get; }
    public IAsyncRelayCommand NextCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand BrowseDirectoryCommand { get; }
    public IRelayCommand AddCustomFormatCommand { get; }

    public CreateRepositoryWindowViewModel(
        IMediator mediator,
        IWindowService windows,
        ILogger<CreateRepositoryWindowViewModel> log)
    {
        _mediator = mediator;
        _windows = windows;
        _log = log;

        BackCommand = new RelayCommand(Back, CanBack);
        NextCommand = new AsyncRelayCommand(NextAsync, CanNext);
        CancelCommand = new RelayCommand(Cancel);
        BrowseDirectoryCommand = new AsyncRelayCommand(BrowseDirectoryAsync, () => !IsBusy);
        AddCustomFormatCommand = new RelayCommand(AddCustomFormat, () => !IsBusy);

        AddDefaultFormats();
        SetStep(0);
    }

    public string Title => _stepIndex switch
    {
        0 => "Новый репозиторий",
        1 => "Форматы отслеживания",
        _ => "Сканирование и сохранение"
    };

    public string StepInfo => $"Шаг {_stepIndex + 1} из 3";

    public bool IsStepRepository => _stepIndex == 0;
    public bool IsStepFormats => _stepIndex == 1;
    public bool IsStepScan => _stepIndex == 2;

    public string NextButtonText
    {
        get
        {
            if (IsStepScan)
                return IsCompleted ? "Готово" : "Закрыть";

            return "Далее";
        }
    }

    public IEnumerable<string> SelectedFormats => Formats
        .Where(f => f.IsSelected)
        .Select(f => f.Format)
        .Distinct(StringComparer.OrdinalIgnoreCase);

    private bool CanBack() => !IsBusy && _stepIndex > 0 && !IsCompleted;

    private bool CanNext()
    {
        if (IsBusy)
            return false;

        if (IsStepRepository)
            return ValidateRepositoryStep();

        if (IsStepFormats)
            return SelectedFormats.Any();

        return true;
    }

    partial void OnRepositoryNameChanged(string value) => RefreshCommands();
    partial void OnDirectoryPathChanged(string value) => RefreshCommands();
    partial void OnIsBusyChanged(bool value) => RefreshCommands();
    partial void OnCustomFormatChanged(string value) => RefreshCommands();

    private void RefreshCommands()
    {
        BackCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        BrowseDirectoryCommand.NotifyCanExecuteChanged();
        AddCustomFormatCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(NextButtonText));
    }

    private void Back()
    {
        if (_stepIndex <= 0)
            return;

        SetStep(_stepIndex - 1);
    }

    private async Task NextAsync()
    {
        ErrorMessage = null;

        if (IsStepRepository)
        {
            SetStep(1);
            return;
        }

        if (IsStepFormats)
        {
            SetStep(2);
            await CreateRepositoryAsync();
            return;
        }

        RequestClose?.Invoke();
    }

    private void Cancel() => RequestClose?.Invoke();

    private async Task BrowseDirectoryAsync()
    {
        Window? owner = _windows.GetActiveWindow();
        if (owner is null)
            return;

        IReadOnlyList<IStorageFolder> res = await owner.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Выберите директорию", AllowMultiple = false });

        var local = res.FirstOrDefault()?.Path.LocalPath;
        if (!string.IsNullOrWhiteSpace(local) && Directory.Exists(local))
        {
            DirectoryPath = local;
            if (string.IsNullOrWhiteSpace(RepositoryName))
                RepositoryName = Path.GetFileName(local.TrimEnd('\\', '/'));
        }
    }

    private void AddCustomFormat()
    {
        if (IsBusy)
            return;

        var normalized = RepositoryFormatOptionViewModel.NormalizeFormat(CustomFormat);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        if (!Formats.Any(x => x.Format.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            var vm = new RepositoryFormatOptionViewModel(normalized, true);
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(RepositoryFormatOptionViewModel.IsSelected))
                    RefreshCommands();
            };

            Formats.Add(vm);
        }
        else
        {
            var existing = Formats.First(x => x.Format.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            existing.IsSelected = true;
        }

        CustomFormat = string.Empty;
        RefreshCommands();
    }

    private async Task CreateRepositoryAsync()
    {
        try
        {
            IsBusy = true;
            IsCompleted = false;
            ErrorMessage = null;
            ProgressPercent = 0;
            ProgressMessage = "Запуск процесса";
            FilesProcessed = 0;
            FilesTotal = 0;

            var progress = new Progress<RepositoryCreationProgressDto>(p =>
            {
                ProgressPercent = Math.Clamp(p.Percent, 0, 100);
                ProgressMessage = p.Message;
                FilesProcessed = p.FilesProcessed;
                FilesTotal = p.FilesTotal;
            });

            var result = await _mediator.Send(new CreateRepositoryWithFormatsCommand(
                RepositoryName,
                Description,
                DirectoryPath,
                SelectedFormats.ToList(),
                progress));

            if (!result.Success)
            {
                ErrorMessage = result.Error ?? "Не удалось создать репозиторий.";
                ProgressMessage = "Ошибка создания";
                return;
            }

            ProgressPercent = 100;
            ProgressMessage = "Репозиторий успешно создан";
            IsCompleted = true;
            RefreshCommands();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to create repository in wizard");
            ErrorMessage = "Не удалось завершить создание репозитория.";
            ProgressMessage = "Ошибка создания";
        }
        finally
        {
            IsBusy = false;
            RefreshCommands();
        }
    }

    private bool ValidateRepositoryStep()
    {
        if (string.IsNullOrWhiteSpace(RepositoryName))
            return false;

        if (string.IsNullOrWhiteSpace(DirectoryPath))
            return false;

        return Directory.Exists(DirectoryPath);
    }

    private void SetStep(int index)
    {
        _stepIndex = Math.Clamp(index, 0, 2);

        OnPropertyChanged(nameof(IsStepRepository));
        OnPropertyChanged(nameof(IsStepFormats));
        OnPropertyChanged(nameof(IsStepScan));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(StepInfo));
        OnPropertyChanged(nameof(NextButtonText));

        RefreshCommands();
    }

    private void AddDefaultFormats()
    {
        foreach (var ext in new[]
                 {
                     ".docx", ".pdf", ".txt", ".rtf", ".odt", ".xlsx",
                     ".png", ".jpg", ".jpeg", ".gif", ".svg",
                     ".json", ".xml", ".cs", ".js", ".ts", ".java", ".py", ".md"
                 })
        {
            var vm = new RepositoryFormatOptionViewModel(ext, true);
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(RepositoryFormatOptionViewModel.IsSelected))
                    RefreshCommands();
            };

            Formats.Add(vm);
        }
    }
}
