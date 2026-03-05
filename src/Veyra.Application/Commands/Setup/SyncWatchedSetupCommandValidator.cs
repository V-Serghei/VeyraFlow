using FluentValidation;

namespace Veyra.Application.Commands.Setup;

public sealed class SyncWatchedSetupCommandValidator : AbstractValidator<SyncWatchedSetupCommand>
{
    public SyncWatchedSetupCommandValidator()
    {
        RuleFor(x => x.Directories).NotNull();
        RuleFor(x => x.Extensions).NotNull();
    }
}
