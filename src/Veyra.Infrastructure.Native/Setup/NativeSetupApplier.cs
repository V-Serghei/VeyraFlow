using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;

namespace Veyra.Infrastructure.Native.Setup;

public sealed class NativeSetupApplier(ILogger<NativeSetupApplier> log) : INativeSetupApplier
{
    public Task ApplySetupAsync(CancellationToken ct = default)
    {
        log.LogDebug("Native setup apply requested");
        return Task.CompletedTask;
    }
}
