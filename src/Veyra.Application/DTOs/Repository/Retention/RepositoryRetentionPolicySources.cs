namespace Veyra.Application.DTOs;

public static class RepositoryRetentionPolicySources
{
    public const string None = "none";
    public const string Global = "global";
    public const string Repository = "repository";
    public const string ParentRepository = "parent_repository";
    public const string NestedRepositoryOverride = "nested_repository_override";

    public static string Normalize(string? value)
    {
        if (string.Equals(value, Global, StringComparison.OrdinalIgnoreCase))
            return Global;

        if (string.Equals(value, Repository, StringComparison.OrdinalIgnoreCase))
            return Repository;

        if (string.Equals(value, ParentRepository, StringComparison.OrdinalIgnoreCase))
            return ParentRepository;

        if (string.Equals(value, NestedRepositoryOverride, StringComparison.OrdinalIgnoreCase))
            return NestedRepositoryOverride;

        return None;
    }
}
