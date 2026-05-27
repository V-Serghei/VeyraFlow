using Veyra.Infrastructure.Native.Interop;

namespace Veyra.Infrastructure.Native;

public static class NativeImageDiffInterop
{
    public static string RenderImageDiffJson(
        string baselineFilePath,
        string currentFilePath,
        int sensitivityPercent,
        int mode,
        int splitPercent,
        bool showRegionBoxes)
        => VeyraCoreNative.RenderImageDiffJson(
            baselineFilePath,
            currentFilePath,
            sensitivityPercent,
            mode,
            splitPercent,
            showRegionBoxes);
}
