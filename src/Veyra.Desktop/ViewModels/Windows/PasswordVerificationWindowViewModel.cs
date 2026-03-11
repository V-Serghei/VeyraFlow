using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class PasswordVerificationWindowViewModel : ObservableObject
{
    public event Action? RequestClose;

    [ObservableProperty] private string _titleText = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private string _usernameText = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _isConfirmed;

    public void Configure(string titleText, string message, string username)
    {
        TitleText = titleText;
        Message = message;
        UsernameText = Loc.F("security.password_prompt_username", username);
        Password = string.Empty;
        IsConfirmed = false;
    }

    public bool CanConfirm => !string.IsNullOrWhiteSpace(Password);

    partial void OnPasswordChanged(string value)
    {
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        IsConfirmed = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        IsConfirmed = false;
        Password = string.Empty;
        RequestClose?.Invoke();
    }
}
