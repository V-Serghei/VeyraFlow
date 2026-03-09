using FluentValidation;

namespace Veyra.Application.Commands.Security;

public sealed class RotateArtifactKeyCommandValidator : AbstractValidator<RotateArtifactKeyCommand>
{
    public RotateArtifactKeyCommandValidator()
    {
        RuleFor(x => x.Note)
            .MaximumLength(512)
            .When(x => !string.IsNullOrWhiteSpace(x.Note))
            .WithMessage("Rotation note is too long.");
    }
}
