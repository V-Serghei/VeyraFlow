using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using MediatR;
using Veyra.Desktop.Native;

namespace Veyra.Desktop.ViewModels.Pages.SetupWizard;

public sealed class SetupWizardViewModel : INotifyPropertyChanged
{

    private readonly IMediator _mediator;
    private readonly SelectDirectoriesViewModel _dirsVm;
    private readonly SelectFormatsViewModel _formatsVm;
    public RelayCommand BackCommand { get; }
    public RelayCommand NextCommand { get; }
    private readonly IList<Control> _pages;
    private int _index;

    public event System.EventHandler? RequestClose;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    /// <summary>
    /// Constructor for SetupWizardViewModel.
    /// This view model manages the navigation and state of a multi-step setup wizard.
    /// </summary>
    /// <param name="mediator"></param>
    /// <param name="dirsVm"></param>
    /// <param name="formatsVm"></param>
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
        NextCommand = new RelayCommand(async _ => await NextAsync(), _ => CanGoNext);

        _dirsVm.SelectionChanged += (_, _) => RefreshNext();
        _formatsVm.SelectionChanged += (_, _) => RefreshNext();
    }

    /// <summary>
    /// Current page being displayed in the wizard.
    /// </summary>
    public Control CurrentPage => _pages[_index];

    /// <summary>
    /// Gets a value indicating whether the "Next" button can be enabled.
    /// </summary>
    public bool CanGoNext =>
        (_pages[_index].DataContext is SelectDirectoriesViewModel d && d.HasAny) ||
        (_pages[_index].DataContext is SelectFormatsViewModel f && f.HasAny);

    /// <summary>
    /// Refreshes the state of the "Next" button.
    /// </summary>
    private void RefreshNext()
    {
        OnPropertyChanged(nameof(CanGoNext));
        NextCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Back navigation logic.
    /// </summary>
    private void Back()
    {
        if (_index == 0) return;
        _index--;
        OnPropertyChanged(nameof(CurrentPage));
        BackCommand.RaiseCanExecuteChanged();
        RefreshNext();
    }

    /// <summary>
    /// Next navigation logic.
    /// </summary>
    private async Task NextAsync()
    {
        if (_pages[_index].DataContext is SelectDirectoriesViewModel)
        {
            if (!await _dirsVm.CommitAsync()) return;
            _index++;
            OnPropertyChanged(nameof(CurrentPage));
            BackCommand.RaiseCanExecuteChanged();
            RefreshNext();
            return;
        }

        if (_pages[_index].DataContext is SelectFormatsViewModel)
        {
            if (!await _formatsVm.CommitAsync()) return;

            // TODO(native/rust): start scan
            RequestClose?.Invoke(this, System.EventArgs.Empty);
        }
    }
}
