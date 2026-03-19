namespace Veyra.Infrastructure.Data.Setup.Models.Integrity;

internal sealed record VerificationOutcome(
    VerificationState State,
    string Message,
    string? LocalPath,
    bool Repaired);
