using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class ConfirmActionWindowViewModel : ObservableObject
{
    public event Action? RequestClose;

    [ObservableProperty] private string _titleText = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private string _warningText = string.Empty;
    [ObservableProperty] private string _confirmButtonText = string.Empty;
    [ObservableProperty] private bool _isConfirmed;

    public void Configure(string titleText, string message, string warningText, string confirmButtonText)
    {
        TitleText = titleText;
        Message = message;
        WarningText = warningText;
        ConfirmButtonText = confirmButtonText;
        IsConfirmed = false;
    }

    [RelayCommand]
    private void Confirm()
    {
        IsConfirmed = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        IsConfirmed = false;
        RequestClose?.Invoke();
    }
}
