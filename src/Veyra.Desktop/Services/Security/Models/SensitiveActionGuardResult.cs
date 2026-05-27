namespace Veyra.Desktop.Services.Security;

public sealed record SensitiveActionGuardResult(bool IsAllowed, bool IsCancelled, string? ErrorMessage = null);
