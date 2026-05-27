using FluentValidation;

namespace Veyra.Application.Commands.Repository;

public sealed class CreateRepositoryWithFormatsCommandValidator
    : AbstractValidator<CreateRepositoryWithFormatsCommand>
{
    public CreateRepositoryWithFormatsCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Repository name is required.")
            .MaximumLength(256).WithMessage("Repository name is too long.");

        RuleFor(x => x.DirectoryPath)
            .NotEmpty().WithMessage("Directory path is required.");

        RuleFor(x => x.Formats)
            .NotEmpty().WithMessage("At least one format is required.")
            .Must(formats => formats
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.StartsWith('.') ? x.ToLowerInvariant() : "." + x.ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() == formats.Count)
            .WithMessage("Formats should be unique.");
    }
}
