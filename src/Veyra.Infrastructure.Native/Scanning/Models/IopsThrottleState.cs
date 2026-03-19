using System;

namespace Veyra.Infrastructure.Native.Scanning;

internal sealed class IopsThrottleState
{
    public DateTime StartUtc { get; } = DateTime.UtcNow;
    public long TotalOperations { get; set; }
}
