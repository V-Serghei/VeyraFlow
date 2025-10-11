using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Labs.Input;
using MediatR;
using Veyra.Desktop.Native;

namespace Veyra.Desktop.ViewModels.Pages.SetupWizard;

public sealed class SetupWizardViewModel : INotifyPropertyChanged
{
    private readonly IMediator _mediator;
    private readonly SelectDirectoriesViewModel _dirsVm;
    private readonly SelectFormatsViewModel _formatsVm;

    private readonly IList<Control> _pages;
    private int _index;

    public SetupWizardViewModel(IMediator mediator,
        SelectDirectoriesViewModel dirsVm,
        SelectFormatsViewModel formatsVm)
    {
        _mediator = mediator;
        _dirsVm = dirsVm;
        _formatsVm = formatsVm;

        _pages = new List<Control>
        {
            new Views.Pages.SetupWizard.SelectDirectoriesPage { DataContext = _dirsVm },
            new Views.Pages.SetupWizard.SelectFormatsPage { DataContext = _formatsVm }
        };

        _index = 0;

        BackCommand = new RelayCommand(_ => Back(), _ => _index > 0);
        NextCommand = new RelayCommand(async void (_) =>
        {
            try
            {
                await NextAsync();
            }
            catch (Exception e)
            {
                throw; // TODO handle exception
            }
        }, _ => CanGoNext);
    }

    public Control CurrentPage => _pages[_index];

    public bool CanGoNext =>
        (_pages[_index].DataContext is SelectDirectoriesViewModel d && d.HasAny)
        || (_pages[_index].DataContext is SelectFormatsViewModel f && f.HasAny);

    public ICommand BackCommand { get; }
    public ICommand NextCommand { get; }

    private void Back()
    {
        if (_index == 0) return;
        _index--;
        OnPropertyChanged(nameof(CurrentPage));
        CommandManager.InvalidateRequerySuggested();
    }

    private async System.Threading.Tasks.Task NextAsync()
    {
        if (_pages[_index].DataContext is SelectDirectoriesViewModel)
        {
            if (!await _dirsVm.CommitAsync()) return;
            _index++;
            OnPropertyChanged(nameof(CurrentPage));
            CommandManager.InvalidateRequerySuggested();
            return;
        }

        if (_pages[_index].DataContext is SelectFormatsViewModel)
        {
            if (!await _formatsVm.CommitAsync()) return;

            // TODO(native/rust): start scan
            RequestClose?.Invoke(this, System.EventArgs.Empty);
        }
    }

    public event System.EventHandler? RequestClose;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
