using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Veyra.Application.Commands.Repository;
using Veyra.Application.DTOs;
using Veyra.Application.Queries;

namespace Veyra.Desktop.Services.Maintenance;

public sealed class RepositoryRetentionDefaultsApplier(
    IMediator mediator,
    IRetentionDefaultsStore retentionDefaultsStore)
    : IRepositoryRetentionDefaultsApplier
{
    private const string SafeDefaultTriggerFilter = "automatic";

    public Task<int> ApplyToRepositoryAsync(int repositoryId, CancellationToken ct = default)
        => ApplyToRepositoriesAsync([repositoryId], ct);

    public async Task<int> ApplyToRepositoriesAsync(IEnumerable<int> repositoryIds, CancellationToken ct = default)
    {
        var settings = await retentionDefaultsStore.LoadAsync(ct)
            ?? new RetentionDefaultsUserSettings(false, null, null, null, null, 60);
        if (!settings.Enabled)
            return 0;

        var policy = settings.ToPolicy();
        var updated = 0;

        foreach (var repositoryId in repositoryIds.Distinct())
        {
            ct.ThrowIfCancellationRequested();

            var detail = await mediator.Send(new GetRepositoryDetailQuery(repositoryId), ct);
            if (detail is null)
                continue;

            var effectiveTriggers = policy.TriggerFilters.Count == 0
                ? [SafeDefaultTriggerFilter]
                : policy.TriggerFilters;

            var effectivePolicy = new RepositoryRetentionPolicyDto(
                Enabled: detail.RetentionPolicy.Enabled,
                MaxAgeDays: policy.MaxAgeDays,
                MaxSnapshots: policy.MaxSnapshots,
                MaxTotalSizeBytes: policy.MaxTotalSizeBytes,
                TriggerFilters: effectiveTriggers,
                RunIntervalMinutes: policy.RunIntervalMinutes,
                MaintenanceWindowStartHour: detail.RetentionPolicy.MaintenanceWindowStartHour,
                MaintenanceWindowEndHour: detail.RetentionPolicy.MaintenanceWindowEndHour,
                LastRunAtUtc: detail.RetentionPolicy.LastRunAtUtc,
                LastStatus: detail.RetentionPolicy.LastStatus,
                StorageMode: policy.StorageMode,
                AllowManualSnapshotCleanup: detail.RetentionPolicy.AllowManualSnapshotCleanup,
                AutomaticCompactionEnabled: detail.RetentionPolicy.AutomaticCompactionEnabled,
                AutomaticCompactionWindowHours: detail.RetentionPolicy.AutomaticCompactionWindowHours);

            var result = await mediator.Send(new UpdateRepositoryConfigurationCommand(
                detail.Id,
                detail.Name,
                detail.Description,
                detail.DirectoryPath,
                detail.LinkedFormats,
                detail.AutoCaptureFileVersions,
                detail.ProtectCloudMetadata,
                detail.ExcludedPatterns,
                effectivePolicy), ct);

            if (result.Success)
                updated++;
        }

        return updated;
    }
}
