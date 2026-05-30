using FluentValidation;

namespace Veyra.Application.Commands.Setup;

public sealed class AddWatchedDirectoryCommandValidator : AbstractValidator<AddWatchedDirectoryCommand>
{
    public AddWatchedDirectoryCommandValidator()
    {
        RuleFor(x => x.Path)
            .NotEmpty().WithMessage("Path must not be empty.")
            .Must(Directory.Exists).WithMessage("Directory does not exist.")
            .Must(path => !IsRootDrive(path)).WithMessage("Root drives (C:\\, D:\\) are not allowed.");
    }

    private static bool IsRootDrive(string path)
    {
        try
        {
            return Path.GetPathRoot(path)?.TrimEnd('\\').Equals(path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true;
        }
        catch
        {
            return false;
        }
    }
}
