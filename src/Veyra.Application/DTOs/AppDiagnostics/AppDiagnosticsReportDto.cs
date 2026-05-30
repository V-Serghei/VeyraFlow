namespace Veyra.Application.DTOs.AppDiagnostics;

public sealed record AppDiagnosticsReportDto(
    DateTime GeneratedAtUtc,
    int? TargetRepositoryId,
    string? TargetRepositoryName,
    string? TargetRepositoryPath,
    AppDiagnosticsProcessMetricsDto Process,
    AppDiagnosticsHistoryMetricsDto History,
    AppDiagnosticsScanBenchmarkDto Scan,
    AppDiagnosticsNativeRuntimeDto NativeRuntime)
{
    public bool HasTargetRepository => TargetRepositoryId.HasValue;
}
