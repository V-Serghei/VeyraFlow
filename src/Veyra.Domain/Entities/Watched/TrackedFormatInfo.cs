namespace Veyra.Domain.Entities.Watched;

public sealed record TrackedFormatInfo(
    int Id,
    string Pattern,
    bool IsEnabled,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<string> Directories);
