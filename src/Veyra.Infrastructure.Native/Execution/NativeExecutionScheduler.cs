using Microsoft.Extensions.Logging;

namespace Veyra.Infrastructure.Native.Execution;

public sealed class NativeExecutionScheduler : INativeExecutionScheduler, IDisposable
{
    private readonly SemaphoreSlim _gate;
    private readonly ILogger<NativeExecutionScheduler> _log;

    public NativeExecutionScheduler(ILogger<NativeExecutionScheduler> log)
    {
        _log = log;
        MaxConcurrency = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
        _gate = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);

        _log.LogInformation(
            "Native execution scheduler initialized. MaxConcurrency {MaxConcurrency}",
            MaxConcurrency);
    }

    public int MaxConcurrency { get; }

    public async Task<T> RunAsync<T>(Func<T> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(work, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task RunAsync(Action work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        return RunAsync(() =>
        {
            work();
            return true;
        }, ct);
    }

    public void Dispose() => _gate.Dispose();
}
