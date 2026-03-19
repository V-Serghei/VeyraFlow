using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.ViewModels.Pages.SetupWizard;

public sealed class RepositoryNameViewModel : INotifyPropertyChanged
{
    public RepositoryNameViewModel()
    {
        LocalizationManager.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Subtitle));
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

    public event EventHandler? SelectionChanged;

    private string _repositoryName = string.Empty;
    public string RepositoryName
    {
        get => _repositoryName;
        set
        {
            if (_repositoryName == value) return;
            _repositoryName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAny));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool HasAny => !string.IsNullOrWhiteSpace(RepositoryName);

    public string Title => Loc.T("setup.repository_name");
    public string Subtitle => Loc.T("setup.repository_name_subtitle");
}
