namespace Veyra.Application.DTOs;

public static class ArtifactKeyStatus
{
    public const string Active = "active";
    public const string Retired = "retired";
    public const string Revoked = "revoked";

    public static IReadOnlyList<string> All { get; } =
    [
        Active,
        Retired,
        Revoked
    ];

    public static string Normalize(string? value)
    {
        if (string.Equals(value, Retired, StringComparison.OrdinalIgnoreCase))
            return Retired;

        if (string.Equals(value, Revoked, StringComparison.OrdinalIgnoreCase))
            return Revoked;

        return Active;
    }
}
