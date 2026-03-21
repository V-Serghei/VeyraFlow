namespace Veyra.Application.DTOs;

public static class RepositoryRetentionStorageModes
{
    public const string Delete = "delete";
    public const string Archive = "archive";

    public static string Normalize(string? value)
    {
        return string.Equals(value, Archive, StringComparison.OrdinalIgnoreCase)
            ? Archive
            : Delete;
    }

    public static bool IsArchive(string? value)
        => string.Equals(Normalize(value), Archive, StringComparison.OrdinalIgnoreCase);
}
