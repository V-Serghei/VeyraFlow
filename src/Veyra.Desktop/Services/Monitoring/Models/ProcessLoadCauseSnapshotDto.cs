namespace Veyra.Desktop.Services.Monitoring.Models;

public sealed record ProcessLoadCauseSnapshotDto(
    string Code,
    string? Subject = null);
