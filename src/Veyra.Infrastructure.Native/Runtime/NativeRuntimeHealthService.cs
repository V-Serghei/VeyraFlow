using Veyra.Application.Abstractions.Observability;
using Veyra.Application.DTOs;
using Veyra.Infrastructure.Native.Runtime;

namespace Veyra.Infrastructure.Native;

public sealed class NativeRuntimeHealthService : INativeRuntimeHealthService
{
    public NativeRuntimeHealthDto Probe()
    {
        var health = NativeRuntimeHealth.Probe();
        return new NativeRuntimeHealthDto(
            health.IsHealthy,
            health.SupportsImageDiff,
            health.SupportsTextDiff,
            health.LoadedPath,
            health.ErrorMessage,
            health.MissingEntrypoints);
    }
}
