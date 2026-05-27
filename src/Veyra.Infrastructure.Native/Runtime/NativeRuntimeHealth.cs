using Veyra.Infrastructure.Native.Interop;

namespace Veyra.Infrastructure.Native;

public static class NativeRuntimeHealth
{
    private static readonly Lazy<NativeRuntimeHealthReport> Cached = new(
        VeyraCoreNative.ProbeRuntimeHealth,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static NativeRuntimeHealthReport Probe() => Cached.Value;
}
