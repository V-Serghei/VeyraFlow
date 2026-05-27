namespace Veyra.Desktop.ViewModels.Pages.Settings;

public sealed record AppUserProfileItemViewModel(
    string Username,
    string EmailText,
    bool IsActive,
    bool HasAccessToken,
    string LastLoginText,
    string CloudUserText,
    string CloudUserLabel,
    string TokenStateText);
