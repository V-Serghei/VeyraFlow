using FluentValidation;

namespace Veyra.Application.Commands.Repository;

public sealed class RepairRepositoryDataCommandValidator : AbstractValidator<RepairRepositoryDataCommand>
{
    public RepairRepositoryDataCommandValidator()
    {
        RuleFor(x => x.RepositoryId)
            .GreaterThan(0)
            .WithMessage("Repository id must be positive.");
    }
}
