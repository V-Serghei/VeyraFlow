using FluentValidation;
using MediatR;

namespace Veyra.Application.Common.Behaviors;

public sealed class ValidationBehavior<TReq, TRes> : IPipelineBehavior<TReq, TRes>
{
    private readonly IEnumerable<IValidator<TReq>> _validators;
    public ValidationBehavior(IEnumerable<IValidator<TReq>> validators) => _validators = validators;

    public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
    {
        if (_validators.Any())
        {
            var ctx = new ValidationContext<TReq>(request);
            var errors = (await Task.WhenAll(_validators.Select(v => v.ValidateAsync(ctx, ct))))
                .SelectMany(r => r.Errors).Where(e => e != null).ToArray();
            if (errors.Length > 0)
                throw new ValidationException(errors);
        }
        return await next();
    }
}
