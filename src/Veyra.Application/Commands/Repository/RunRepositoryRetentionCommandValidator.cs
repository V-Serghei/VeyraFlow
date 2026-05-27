using FluentValidation;

namespace Veyra.Application.Commands.Repository;

public sealed class RunRepositoryRetentionCommandValidator
    : AbstractValidator<RunRepositoryRetentionCommand>
{
    public RunRepositoryRetentionCommandValidator()
    {
        RuleFor(x => x.RepositoryId)
            .GreaterThan(0)
            .WithMessage("Repository id must be positive.");
    }
}
