using FluentValidation;

namespace Veyra.Application.Commands.Security;

public sealed class RevokeArtifactKeyCommandValidator : AbstractValidator<RevokeArtifactKeyCommand>
{
    public RevokeArtifactKeyCommandValidator()
    {
        RuleFor(x => x.KeyId)
            .NotEmpty()
            .WithMessage("Key id is required.");
    }
}
