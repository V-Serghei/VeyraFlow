using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Auth;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.ViewModels.Windows;
using Veyra.Desktop.Views.Windows;

namespace Veyra.Desktop.Services.Security;

public sealed class SensitiveActionGuard(
    IUserProfileRepository userProfiles,
    IAuthService auth,
    IWindowService windows,
    ILogger<SensitiveActionGuard> log) : ISensitiveActionGuard
{
    public async Task<SensitiveActionGuardResult> AuthorizeIfRequiredAsync(
        string actionTitle,
        string actionDescription,
        CancellationToken ct = default)
    {
        var activeProfile = await userProfiles.GetActiveProfileAsync(ct);
        if (activeProfile?.RequirePasswordForSensitiveActions != true)
            return new SensitiveActionGuardResult(true, false);

        if (string.IsNullOrWhiteSpace(activeProfile.Username))
            return new SensitiveActionGuardResult(false, false, Loc.T("security.error_sign_in_required"));

        var owner = windows.GetActiveWindow();
        if (owner is null)
            return new SensitiveActionGuardResult(false, false, Loc.T("security.error_window_unavailable"));

        var prompt = windows.Create<PasswordVerificationWindow>();
        if (prompt.DataContext is PasswordVerificationWindowViewModel vm)
            vm.Configure(actionTitle, actionDescription, activeProfile.Username);

        await windows.ShowDialogAsync(prompt, owner);

        if (prompt.DataContext is not PasswordVerificationWindowViewModel resultVm)
            return new SensitiveActionGuardResult(false, false, Loc.T("security.error_verification_failed"));

        if (!resultVm.IsConfirmed)
            return new SensitiveActionGuardResult(false, true);

        try
        {
            var session = await auth.LoginAsync(activeProfile.Username, resultVm.Password, ct);
            if (session is null)
                return new SensitiveActionGuardResult(false, false, Loc.T("security.error_invalid_password"));

            if (!string.IsNullOrWhiteSpace(session.AccessToken))
            {
                try
                {
                    await auth.LogoutAsync(session.AccessToken, ct);
                }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "Temporary verification logout failed for {Username}", activeProfile.Username);
                }
            }

            return new SensitiveActionGuardResult(true, false);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Sensitive action verification failed for {Username}", activeProfile.Username);
            return new SensitiveActionGuardResult(false, false, Loc.T("security.error_verification_failed"));
        }
    }
}
