using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Storage;

public interface ILocalBlockStorageMetricsService
{
    Task<LocalBlockStorageMetricsDto> GetMetricsAsync(CancellationToken ct = default);
}
