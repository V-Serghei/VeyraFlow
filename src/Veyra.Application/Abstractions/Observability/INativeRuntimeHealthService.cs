using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Observability;

public interface INativeRuntimeHealthService
{
    NativeRuntimeHealthDto Probe();
}
