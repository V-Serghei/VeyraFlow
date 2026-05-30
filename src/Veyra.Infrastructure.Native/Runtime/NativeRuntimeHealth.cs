using Veyra.Infrastructure.Native.Interop;
using Veyra.Infrastructure.Native.Runtime.Models;

namespace Veyra.Infrastructure.Native.Runtime;

public static class NativeRuntimeHealth
{
    private static readonly Lazy<NativeRuntimeHealthReport> Cached = new(
        VeyraCoreNative.ProbeRuntimeHealth,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static NativeRuntimeHealthReport Probe() => Cached.Value;
}
