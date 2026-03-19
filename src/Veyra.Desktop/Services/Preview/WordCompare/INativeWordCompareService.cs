using System.Threading;
using System.Threading.Tasks;

namespace Veyra.Desktop.Services.Preview;

public interface INativeWordCompareService
{
    bool IsAvailable { get; }

    Task<NativeWordCompareLaunchResult> OpenCompareAsync(
        string leftFilePath,
        string rightFilePath,
        NativeWordCompareOptions? options = null,
        CancellationToken ct = default);
}
