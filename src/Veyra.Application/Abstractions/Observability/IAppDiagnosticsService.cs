using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Observability;

public interface IAppDiagnosticsService
{
    public Task<AppDiagnosticsReportDto> RunAsync(CancellationToken ct = default);
}
