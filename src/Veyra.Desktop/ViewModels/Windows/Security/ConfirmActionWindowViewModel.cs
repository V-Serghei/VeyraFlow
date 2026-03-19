using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class ConfirmActionWindowViewModel : ObservableObject
{
    private string? _titleKey;
    private string _titleText = string.Empty;
    private string? _messageKey;
    private object[] _messageArgs = [];
    private string _messageText = string.Empty;
    private string? _warningKey;
    private string _warningText = string.Empty;
    private string? _confirmButtonKey;
    private string _confirmButtonText = string.Empty;

    public event Action? RequestClose;

    [ObservableProperty] private bool _isConfirmed;

    public ConfirmActionWindowViewModel()
    {
        LocalizationManager.Instance.LanguageChanged += (_, _) => RefreshLocalizationState();
    }

    public string TitleText => ResolveText(_titleKey, _titleText);
    public string Message => ResolveText(_messageKey, _messageText, _messageArgs);
    public string WarningText => ResolveText(_warningKey, _warningText);
    public string ConfirmButtonText => ResolveText(_confirmButtonKey, _confirmButtonText);
    public string WindowTitleText => Loc.T("common.confirm");
    public string CancelButtonText => Loc.T("common.cancel");

    public void Configure(string titleText, string message, string warningText, string confirmButtonText)
    {
        _titleKey = null;
        _titleText = titleText;
        _messageKey = null;
        _messageArgs = [];
        _messageText = message;
        _warningKey = null;
        _warningText = warningText;
        _confirmButtonKey = null;
        _confirmButtonText = confirmButtonText;
        IsConfirmed = false;
        RefreshLocalizationState();
    }

    public void ConfigureLocalized(
        string titleKey,
        string messageKey,
        object[]? messageArgs,
        string warningKey,
        string confirmButtonKey)
    {
        _titleKey = titleKey;
        _titleText = string.Empty;
        _messageKey = messageKey;
        _messageArgs = messageArgs ?? [];
        _messageText = string.Empty;
        _warningKey = warningKey;
        _warningText = string.Empty;
        _confirmButtonKey = confirmButtonKey;
        _confirmButtonText = string.Empty;
        IsConfirmed = false;
        RefreshLocalizationState();
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

    private void RefreshLocalizationState()
    {
        OnPropertyChanged(nameof(WindowTitleText));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(WarningText));
        OnPropertyChanged(nameof(CancelButtonText));
        OnPropertyChanged(nameof(ConfirmButtonText));
    }

    private static string ResolveText(string? localizationKey, string fallback, params object[] args)
    {
        if (!string.IsNullOrWhiteSpace(localizationKey))
            return args.Length == 0 ? Loc.T(localizationKey) : Loc.F(localizationKey, args);

        return fallback;
    }
}
