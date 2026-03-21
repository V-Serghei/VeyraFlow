using FluentValidation;

namespace Veyra.Application.Commands.Repository;

public sealed class RunRepositoryIntegrityCheckCommandValidator
    : AbstractValidator<RunRepositoryIntegrityCheckCommand>
{
    public RunRepositoryIntegrityCheckCommandValidator()
    {
        RuleFor(x => x.RepositoryId)
            .GreaterThan(0)
            .WithMessage("Repository id must be positive.");

        RuleFor(x => x.MaxIssueSamples)
            .InclusiveBetween(10, 5000)
            .WithMessage("Max issue samples must be between 10 and 5000.");
    }
}
