using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Veyra.Application.Common.Behaviors;

public sealed class ValidationBehavior<TReq, TRes> : IPipelineBehavior<TReq, TRes>
    where TReq : notnull
{
    private readonly IEnumerable<IValidator<TReq>> _validators;
    private readonly ILogger<ValidationBehavior<TReq, TRes>> _log;

    public ValidationBehavior(
        IEnumerable<IValidator<TReq>> validators,
        ILogger<ValidationBehavior<TReq, TRes>> log)
    {
        _validators = validators;
        _log = log;
    }

    public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
    {
        if (!_validators.Any())
            return await next();

        var ctx = new ValidationContext<TReq>(request);
        var errors = (await Task.WhenAll(_validators.Select(v => v.ValidateAsync(ctx, ct))))
            .SelectMany(r => r.Errors)
            .Where(e => e is not null)
            .ToArray();

        if (errors.Length == 0)
            return await next();

        _log.LogWarning(
            "Validation failed for {RequestName}. Errors {ErrorCount}",
            typeof(TReq).Name,
            errors.Length);

        throw new ValidationException(errors);
    }
}
