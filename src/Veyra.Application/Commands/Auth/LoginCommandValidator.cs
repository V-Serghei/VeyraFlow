using FluentValidation;

namespace Veyra.Application.Commands.Auth;

public class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(x => x.Username)
            .NotEmpty().WithMessage("Username or email is required.")
            .MaximumLength(320).WithMessage("Username or email must not exceed 320 characters.")
            .Must(s => !string.IsNullOrWhiteSpace(s)).WithMessage("Username or email cannot be whitespace.");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.")
            .MinimumLength(4).WithMessage("Password must be at least 4 characters long.")
            .MaximumLength(100).WithMessage("Password must not exceed 100 characters.");
    }
}
