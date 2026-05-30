using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Domain.Observability;

namespace Veyra.Application.Common.Behaviors;

public sealed class LoggingBehavior<TReq, TRes>(
    ILogger<LoggingBehavior<TReq, TRes>> log,
    IRuntimeObservabilityState observability)
    : IPipelineBehavior<TReq, TRes>
    where TReq : notnull
{
    public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
    {
        if (!observability.IsLoggingEnabled)
            return await next();

        var requestName = typeof(TReq).Name;
        var sw = Stopwatch.StartNew();

        log.LogInformation("Handling {RequestName}", requestName);

        try
        {
            var result = await next();
            sw.Stop();

            log.LogInformation("Handled {RequestName} in {ElapsedMs} ms", requestName, sw.ElapsedMilliseconds);
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
            log.LogError(ex, "Request {RequestName} failed in {ElapsedMs} ms", requestName, sw.ElapsedMilliseconds);
            throw;
        }
    }
}
