using Veyra.Infrastructure.Native;

var health = NativeRuntimeHealth.Probe();

Console.WriteLine($"IsLoaded: {health.IsLoaded}");
Console.WriteLine($"IsHealthy: {health.IsHealthy}");
Console.WriteLine($"LoadedPath: {health.LoadedPath ?? "(not loaded)"}");
Console.WriteLine($"Error: {health.ErrorMessage ?? "(none)"}");
Console.WriteLine($"SupportsScan: {health.SupportsScan}");
Console.WriteLine($"SupportsStoreFileBlocks: {health.SupportsStoreFileBlocks}");
Console.WriteLine($"SupportsRestoreFileBlocks: {health.SupportsRestoreFileBlocks}");
Console.WriteLine($"SupportsTextDiff: {health.SupportsTextDiff}");
Console.WriteLine($"SupportsSnapshotComparison: {health.SupportsSnapshotComparison}");
Console.WriteLine($"SupportsRepositoryPathComparison: {health.SupportsRepositoryPathComparison}");
Console.WriteLine($"SupportsVersionPlanning: {health.SupportsVersionPlanning}");

if (health.MissingEntrypoints.Count > 0)
    Console.WriteLine($"MissingEntrypoints: {string.Join(", ", health.MissingEntrypoints)}");

if (health.CandidatePaths.Count > 0)
{
    Console.WriteLine("CandidatePaths:");
    foreach (var path in health.CandidatePaths)
        Console.WriteLine($"  - {path}");
}

return health.IsHealthy ? 0 : 1;
