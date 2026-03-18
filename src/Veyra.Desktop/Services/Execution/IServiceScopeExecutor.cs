using System;
using System.Threading;
using System.Threading.Tasks;

namespace Veyra.Desktop.Services.Execution;

public interface IServiceScopeExecutor
{
    Task<TResult> ExecuteAsync<TService, TResult>(
        Func<TService, CancellationToken, Task<TResult>> operation,
        CancellationToken ct = default)
        where TService : notnull;

    Task ExecuteAsync<TService>(
        Func<TService, CancellationToken, Task> operation,
        CancellationToken ct = default)
        where TService : notnull;
}
