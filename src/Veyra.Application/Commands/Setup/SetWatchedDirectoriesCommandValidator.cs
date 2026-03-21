using FluentValidation;

namespace Veyra.Application.Commands.Setup;

public sealed class SetWatchedDirectoriesCommandValidator : AbstractValidator<SetWatchedDirectoriesCommand>
{
    public SetWatchedDirectoriesCommandValidator()
    {
        RuleFor(x => x.Paths)
            .NotEmpty().WithMessage("Select at least one folder.")
            .Must(list => list.Distinct(System.StringComparer.OrdinalIgnoreCase).Count() == list.Count)
            .WithMessage("Duplicate folders are not allowed.")
            .Must(list => list.All(Directory.Exists))
            .WithMessage("Some folders do not exist.")
            .Must(list => list.All(p => !IsRootDrive(p)))
            .WithMessage("Root drives (C:\\, D:\\) are not allowed.");
    }

    private static bool IsRootDrive(string p)
    {
        try { return Path.GetPathRoot(p)?.TrimEnd('\\')?.Equals(p.TrimEnd('\\'), System.StringComparison.OrdinalIgnoreCase) == true; }
        catch { return false; }
    }
}
