using MediatR;
using Microsoft.Extensions.Logging;

namespace Veyra.Application.Common.Behaviors;

public sealed class LoggingBehavior<TReq, TRes> : IPipelineBehavior<TReq, TRes>
{
    private readonly ILogger<LoggingBehavior<TReq, TRes>> _log;
    public LoggingBehavior(ILogger<LoggingBehavior<TReq,TRes>> log) => _log = log;

    public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
    {
        _log.LogDebug("Handling {Req}", typeof(TReq).Name);
        var res = await next();
        _log.LogDebug("Handled {Req}", typeof(TReq).Name);
        return res;
    }
}
