using FluentValidation;

namespace Veyra.Application.Commands.Setup;

public sealed class SetTrackedExtensionsCommandValidator : AbstractValidator<SetTrackedExtensionsCommand>
{
    public SetTrackedExtensionsCommandValidator()
    {
        RuleFor(x => x.Extensions)
            .NotEmpty().WithMessage("Pick at least one extension.")
            .Must(list => list.All(e => e.StartsWith(".")))
            .WithMessage("Extensions must start with '.'.")
            .Must(list => list.Distinct(System.StringComparer.OrdinalIgnoreCase).Count() == list.Count)
            .WithMessage("Duplicate extensions are not allowed.");
    }
}
