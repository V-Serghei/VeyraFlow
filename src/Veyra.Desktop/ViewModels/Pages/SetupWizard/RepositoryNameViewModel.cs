using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Veyra.Desktop.ViewModels.Pages.SetupWizard;

public sealed class RepositoryNameViewModel : INotifyPropertyChanged
{
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

    public string Title => "Имя репозитория";
    public string Subtitle => "Введите имя для вашего репозитория. Если директорий несколько — каждая получит имя по названию папки, а здесь задаётся имя первого.";
}
