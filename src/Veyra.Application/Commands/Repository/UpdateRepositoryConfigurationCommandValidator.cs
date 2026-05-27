using System;
using System.Linq;
using FluentValidation;
using Veyra.Application.DTOs;

namespace Veyra.Application.Commands.Repository;

public sealed class UpdateRepositoryConfigurationCommandValidator
    : AbstractValidator<UpdateRepositoryConfigurationCommand>
{
    public UpdateRepositoryConfigurationCommandValidator()
    {
        RuleFor(x => x.RepositoryId)
            .GreaterThan(0).WithMessage("Repository id must be positive.");

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Repository name is required.")
            .MaximumLength(256).WithMessage("Repository name is too long.");

        RuleFor(x => x.DirectoryPath)
            .NotEmpty().WithMessage("Directory path is required.");

        RuleFor(x => x.Formats)
            .NotEmpty().WithMessage("At least one format is required.")
            .Must(formats => formats.All(f => !string.IsNullOrWhiteSpace(f)))
            .WithMessage("Formats cannot contain empty values.");

        RuleFor(x => x.RetentionPolicy.RunIntervalMinutes)
            .GreaterThanOrEqualTo(5)
            .WithMessage("Retention interval must be at least 5 minutes.")
            .LessThanOrEqualTo(7 * 24 * 60)
            .WithMessage("Retention interval is too large.");

        RuleFor(x => x.RetentionPolicy.MaxAgeDays)
            .GreaterThan(0)
            .When(x => x.RetentionPolicy.MaxAgeDays.HasValue)
            .WithMessage("Retention max age must be positive.");

        RuleFor(x => x.RetentionPolicy.MaxSnapshots)
            .GreaterThan(0)
            .When(x => x.RetentionPolicy.MaxSnapshots.HasValue)
            .WithMessage("Retention max snapshots must be positive.");

        RuleFor(x => x.RetentionPolicy.MaxTotalSizeBytes)
            .GreaterThan(0)
            .When(x => x.RetentionPolicy.MaxTotalSizeBytes.HasValue)
            .WithMessage("Retention max size must be positive.");

        RuleFor(x => x.RetentionPolicy.AutomaticCompactionWindowHours)
            .GreaterThan(0)
            .When(x => x.RetentionPolicy.AutomaticCompactionWindowHours.HasValue)
            .WithMessage("Automatic compaction window must be positive.");

        RuleFor(x => x.RetentionPolicy.AutomaticCompactionWindowHours)
            .NotNull()
            .When(x => x.RetentionPolicy.AutomaticCompactionEnabled)
            .WithMessage("Automatic compaction requires a compaction window.");

        RuleFor(x => x.RetentionPolicy.StorageMode)
            .Must(mode => mode == RepositoryRetentionStorageModes.Delete || mode == RepositoryRetentionStorageModes.Archive)
            .WithMessage("Retention storage mode is invalid.");

        RuleFor(x => x.RetentionPolicy)
            .Must(policy => !policy.HasLocalOverride || !TargetsManualSnapshots(policy) || policy.AllowManualSnapshotCleanup)
            .WithMessage("Manual snapshot cleanup requires an explicit unlock.");

        RuleFor(x => x.SyncConflictStrategy)
            .Must(v => RepositorySyncConflictStrategies.All.Contains(
                RepositorySyncConflictStrategies.Normalize(v),
                StringComparer.OrdinalIgnoreCase))
            .WithMessage("Sync conflict strategy is invalid.");

        RuleFor(x => x.SyncRetryMaxAttempts)
            .InclusiveBetween(1, 20)
            .WithMessage("Sync retry max attempts must be between 1 and 20.");

        RuleFor(x => x.SyncRetryBaseDelaySeconds)
            .InclusiveBetween(5, 600)
            .WithMessage("Sync retry base delay must be between 5 and 600 seconds.");
    }

    private static bool TargetsManualSnapshots(RepositoryRetentionPolicyDto policy)
    {
        return policy.TriggerFilters.Any(static value =>
            string.Equals(value, "manual", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase));
    }
}
