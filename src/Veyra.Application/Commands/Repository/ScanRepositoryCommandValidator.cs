using FluentValidation;

namespace Veyra.Application.Commands.Repository;

public sealed class ScanRepositoryCommandValidator
    : AbstractValidator<ScanRepositoryCommand>
{
    public ScanRepositoryCommandValidator()
    {
        RuleFor(x => x.RepositoryId)
            .GreaterThan(0).WithMessage("Repository id must be positive.");
    }
}
