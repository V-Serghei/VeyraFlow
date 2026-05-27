using FluentValidation;

namespace Veyra.Application.Commands.Repository;

public sealed class RelinkRepositoryDataCommandValidator : AbstractValidator<RelinkRepositoryDataCommand>
{
    public RelinkRepositoryDataCommandValidator()
    {
        RuleFor(x => x.RepositoryId)
            .GreaterThan(0)
            .WithMessage("Repository id must be positive.");
    }
}

