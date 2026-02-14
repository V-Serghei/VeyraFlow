namespace Veyra.Application.Abstractions.Setup;

public interface INativeSetupApplier
{
    Task ApplySetupAsync(CancellationToken ct = default);
}
