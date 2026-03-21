namespace Veyra.Infrastructure.Data.Setup.Models.Retention;

internal sealed record BlockState(
    long Id,
    string BlockHash,
    long StoredSizeBytes);
