using FluentValidation;

namespace Veyra.Application.Commands.Repository;

public sealed class ReindexRepositoryDataCommandValidator : AbstractValidator<ReindexRepositoryDataCommand>
{
    public ReindexRepositoryDataCommandValidator()
    {
        RuleFor(x => x.RepositoryId)
            .GreaterThan(0)
            .WithMessage("Repository id must be positive.");
    }
}

