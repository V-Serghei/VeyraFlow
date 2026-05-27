using FluentValidation;

namespace Veyra.Application.Commands.Repository;

public sealed class RestoreFileVersionCommandValidator : AbstractValidator<RestoreFileVersionCommand>
{
    public RestoreFileVersionCommandValidator()
    {
        RuleFor(x => x.RepositoryId)
            .GreaterThan(0).WithMessage("Repository id must be positive.");

        RuleFor(x => x.FileVersionId)
            .GreaterThan(0).WithMessage("File version id must be positive.");

        RuleFor(x => x.RelativePath)
            .NotEmpty().WithMessage("Relative path is required.");
    }
}
