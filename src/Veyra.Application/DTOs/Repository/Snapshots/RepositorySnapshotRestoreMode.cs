namespace Veyra.Application.DTOs.Repository.Snapshots;

public static class RepositorySnapshotRestoreMode
{
    public const string Rollback = "rollback";
    public const string Copies = "copies";

    public static string Normalize(string? value)
        => string.Equals(value, Copies, StringComparison.OrdinalIgnoreCase)
            ? Copies
            : Rollback;
}
