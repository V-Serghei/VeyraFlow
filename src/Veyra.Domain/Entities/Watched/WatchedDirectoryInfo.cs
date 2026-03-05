namespace Veyra.Domain.Entities.Watched;

public sealed record WatchedDirectoryInfo(
    int Id,
    string Path,
    bool IsEnabled,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<string> Formats);
