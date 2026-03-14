using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Veyra.Application.Common.Behaviors;

public sealed class LoggingBehavior<TReq, TRes> : IPipelineBehavior<TReq, TRes>
    where TReq : notnull
{
    private readonly ILogger<LoggingBehavior<TReq, TRes>> _log;

    public LoggingBehavior(ILogger<LoggingBehavior<TReq, TRes>> log)
    {
        _log = log;
    }

    public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
    {
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
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex, "Request {RequestName} failed in {ElapsedMs} ms", requestName, sw.ElapsedMilliseconds);
            throw;
        }
    }
}
