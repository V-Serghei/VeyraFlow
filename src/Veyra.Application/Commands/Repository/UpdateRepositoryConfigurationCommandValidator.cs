using FluentValidation;

namespace Veyra.Application.Commands.Repository;

public sealed class UpdateRepositoryConfigurationCommandValidator
    : AbstractValidator<UpdateRepositoryConfigurationCommand>
{
    public UpdateRepositoryConfigurationCommandValidator()
    {
        RuleFor(x => x.RepositoryId)
            .GreaterThan(0).WithMessage("Repository id must be positive.");

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Repository name is required.")
            .MaximumLength(256).WithMessage("Repository name is too long.");

        RuleFor(x => x.DirectoryPath)
            .NotEmpty().WithMessage("Directory path is required.");

        RuleFor(x => x.Formats)
            .NotEmpty().WithMessage("At least one format is required.")
            .Must(formats => formats.All(f => !string.IsNullOrWhiteSpace(f)))
            .WithMessage("Formats cannot contain empty values.");
    }
}
