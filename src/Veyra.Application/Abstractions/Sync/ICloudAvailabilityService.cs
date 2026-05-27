using System;
using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Sync;

public interface ICloudAvailabilityService
{
    CloudAvailabilitySnapshot Snapshot { get; }

    bool CanExecuteCloudOperations { get; }

    bool ShouldSkipCloudOperation(out string reason);

    void ReportCloudSuccess();

    void ReportCloudFailure(Exception ex);
}
