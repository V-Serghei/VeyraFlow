namespace Veyra.Application.DTOs.Repository.Core;

public static class RepositorySyncConflictStrategies
{
    public const string LastWriteWins = "last_write_wins";
    public const string ManualMerge = "manual_merge";
    public const string PreserveBoth = "preserve_both";

    public static IReadOnlyList<string> All { get; } =
    [
        LastWriteWins,
        ManualMerge,
        PreserveBoth
    ];

    public static string Normalize(string? value)
    {
        if (string.Equals(value, ManualMerge, StringComparison.OrdinalIgnoreCase))
            return ManualMerge;

        if (string.Equals(value, PreserveBoth, StringComparison.OrdinalIgnoreCase))
            return PreserveBoth;

        return LastWriteWins;
    }

    public static string ToDisplay(string? value)
    {
        return Normalize(value) switch
        {
            ManualMerge => "Manual merge",
            PreserveBoth => "Preserve both",
            _ => "Last write wins"
        };
    }
}
