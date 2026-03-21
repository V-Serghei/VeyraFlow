using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Domain.Observability;

namespace Veyra.Application.Common.Behaviors;

public sealed class LoggingBehavior<TReq, TRes> : IPipelineBehavior<TReq, TRes>
    where TReq : notnull
{
    private readonly ILogger<LoggingBehavior<TReq, TRes>> _log;
    private readonly IRuntimeObservabilityState _observability;

    public LoggingBehavior(
        ILogger<LoggingBehavior<TReq, TRes>> log,
        IRuntimeObservabilityState observability)
    {
        _log = log;
        _observability = observability;
    }

    public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
    {
        if (!_observability.IsLoggingEnabled)
            return await next();

        var requestName = typeof(TReq).Name;
        var sw = Stopwatch.StartNew();

        _log.LogInformation("Handling {RequestName}", requestName);

        try
        {
            var result = await next();
            sw.Stop();

            _log.LogInformation("Handled {RequestName} in {ElapsedMs} ms", requestName, sw.ElapsedMilliseconds);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            sw.Stop();
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex, "Request {RequestName} failed in {ElapsedMs} ms", requestName, sw.ElapsedMilliseconds);
            throw;
        }
    }
}
