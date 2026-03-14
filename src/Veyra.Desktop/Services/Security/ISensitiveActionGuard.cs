using System.Threading;
using System.Threading.Tasks;

namespace Veyra.Desktop.Services.Security;

public sealed record SensitiveActionGuardResult(bool IsAllowed, bool IsCancelled, string? ErrorMessage = null);

public interface ISensitiveActionGuard
{
    Task<SensitiveActionGuardResult> AuthorizeIfRequiredAsync(
        string actionTitle,
        string actionDescription,
        CancellationToken ct = default);

    Task<SensitiveActionGuardResult> AuthorizeIfRequiredLocalizedAsync(
        string actionTitleKey,
        string actionDescriptionKey,
        object[]? actionDescriptionArgs = null,
        CancellationToken ct = default);
}
