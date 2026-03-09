using FluentValidation;

namespace Veyra.Application.Commands.Repository;

public sealed class ExportRepositoryBundleCommandValidator : AbstractValidator<ExportRepositoryBundleCommand>
{
    public ExportRepositoryBundleCommandValidator()
    {
        RuleFor(x => x.RepositoryId)
            .GreaterThan(0)
            .WithMessage("Repository id must be positive.");

        RuleFor(x => x.BundlePath)
            .NotEmpty()
            .WithMessage("Bundle path is required.");
    }
}
