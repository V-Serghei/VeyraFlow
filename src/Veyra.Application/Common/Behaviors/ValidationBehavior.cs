using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Veyra.Application.Common.Behaviors;

public sealed class ValidationBehavior<TReq, TRes>(
    IEnumerable<IValidator<TReq>> validators,
    ILogger<ValidationBehavior<TReq, TRes>> log)
    : IPipelineBehavior<TReq, TRes>
    where TReq : notnull
{
    public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
    {
        if (!validators.Any())
            return await next(ct);

        var ctx = new ValidationContext<TReq>(request);
        var errors = (await Task.WhenAll(validators.Select(v => v.ValidateAsync(ctx, ct))))
            .SelectMany(r => r.Errors)
            .Where(e => e is not null)
            .ToArray();

        if (errors.Length == 0)
            return await next(ct);

        log.LogWarning(
            "Validation failed for {RequestName}. Errors {ErrorCount}",
            typeof(TReq).Name,
            errors.Length);

        throw new ValidationException(errors);
    }
}
