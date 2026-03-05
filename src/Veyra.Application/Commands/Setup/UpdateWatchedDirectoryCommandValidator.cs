﻿using FluentValidation;

namespace Veyra.Application.Commands.Setup;

public sealed class UpdateWatchedDirectoryCommandValidator : AbstractValidator<UpdateWatchedDirectoryCommand>
{
    public UpdateWatchedDirectoryCommandValidator()
    {
        RuleFor(x => x.OldPath)
            .NotEmpty().WithMessage("Old path must not be empty.");

        RuleFor(x => x.NewPath)
            .NotEmpty().WithMessage("New path must not be empty.")
            .Must(Directory.Exists).WithMessage("New directory does not exist.")
            .Must(p => !IsRootDrive(p)).WithMessage("Root drives (C:\\, D:\\) are not allowed.");
    }

    private static bool IsRootDrive(string p)
    {
        try { return Path.GetPathRoot(p)?.TrimEnd('\\')?.Equals(p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true; }
        catch { return false; }
    }
}
