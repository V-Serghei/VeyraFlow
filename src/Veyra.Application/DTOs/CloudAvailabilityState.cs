namespace Veyra.Application.DTOs;

public enum CloudAvailabilityState
{
    Unknown = 0,
    Online = 1,
    Offline = 2,
    CloudUnavailable = 3,
    Unauthorized = 4,
    Maintenance = 5,
    Reconnecting = 6
}
