using Veyra.Application.DTOs;
using Veyra.Application.DTOs.AppDiagnostics;

namespace Veyra.Application.Abstractions.Observability;

public interface IAppDiagnosticsService
{
    public Task<AppDiagnosticsReportDto> RunAsync(CancellationToken ct = default);
}
