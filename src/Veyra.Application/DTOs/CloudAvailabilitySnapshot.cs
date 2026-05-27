using System;

namespace Veyra.Application.DTOs;

public sealed record CloudAvailabilitySnapshot(
    CloudAvailabilityState State,
    DateTime CheckedAtUtc,
    DateTime? RetryAfterUtc,
    string? Reason = null)
{
    public bool CanExecuteCloudOperations => State is CloudAvailabilityState.Online;
}
