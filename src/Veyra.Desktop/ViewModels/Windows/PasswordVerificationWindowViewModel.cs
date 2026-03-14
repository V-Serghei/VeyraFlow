using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class PasswordVerificationWindowViewModel : ObservableObject
{
    private string? _titleKey;
    private string _titleText = string.Empty;
    private string? _messageKey;
    private object[] _messageArgs = [];
    private string _messageText = string.Empty;
    private string _username = string.Empty;

    public event Action? RequestClose;

    public PasswordVerificationWindowViewModel()
    {
        LocalizationManager.Instance.LanguageChanged += (_, _) => RefreshLocalizationState();
    }

    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _isConfirmed;

    public string TitleText => ResolveText(_titleKey, _titleText);
    public string Message => ResolveText(_messageKey, _messageText, _messageArgs);
    public string UsernameText => string.IsNullOrWhiteSpace(_username)
        ? string.Empty
        : Loc.F("security.password_prompt_username", _username);
    public string WindowTitleText => Loc.T("security.password_prompt_window_title");
    public string PasswordLabelText => Loc.T("auth.password");
    public string PasswordPlaceholderText => Loc.T("auth.password_placeholder");
    public string CancelButtonText => Loc.T("common.cancel");
    public string ContinueButtonText => Loc.T("common.continue");

    public void Configure(string titleText, string message, string username)
    {
        _titleKey = null;
        _titleText = titleText;
        _messageKey = null;
        _messageArgs = [];
        _messageText = message;
        _username = username;
        Password = string.Empty;
        IsConfirmed = false;
        RefreshLocalizationState();
    }

    public void ConfigureLocalized(
        string titleKey,
        string messageKey,
        object[]? messageArgs,
        string username)
    {
        _titleKey = titleKey;
        _titleText = string.Empty;
        _messageKey = messageKey;
        _messageArgs = messageArgs ?? [];
        _messageText = string.Empty;
        _username = username;
        Password = string.Empty;
        IsConfirmed = false;
        RefreshLocalizationState();
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

    private void RefreshLocalizationState()
    {
        OnPropertyChanged(nameof(WindowTitleText));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(UsernameText));
        OnPropertyChanged(nameof(PasswordLabelText));
        OnPropertyChanged(nameof(PasswordPlaceholderText));
        OnPropertyChanged(nameof(CancelButtonText));
        OnPropertyChanged(nameof(ContinueButtonText));
    }

    private static string ResolveText(string? localizationKey, string fallback, params object[] args)
    {
        if (!string.IsNullOrWhiteSpace(localizationKey))
            return args.Length == 0 ? Loc.T(localizationKey) : Loc.F(localizationKey, args);

        return fallback;
    }
}
