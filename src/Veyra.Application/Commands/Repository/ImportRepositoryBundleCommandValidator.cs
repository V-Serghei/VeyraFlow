using FluentValidation;

namespace Veyra.Application.Commands.Repository;

public sealed class ImportRepositoryBundleCommandValidator : AbstractValidator<ImportRepositoryBundleCommand>
{
    public ImportRepositoryBundleCommandValidator()
    {
        RuleFor(x => x.BundlePath)
            .NotEmpty()
            .WithMessage("Bundle path is required.");

        RuleFor(x => x.TargetDirectoryPath)
            .NotEmpty()
            .WithMessage("Target directory path is required.");

        RuleFor(x => x.RepositoryNameOverride)
            .MaximumLength(256)
            .When(x => !string.IsNullOrWhiteSpace(x.RepositoryNameOverride));
    }
}
