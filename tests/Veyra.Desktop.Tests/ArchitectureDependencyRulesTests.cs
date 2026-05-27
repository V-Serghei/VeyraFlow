using System.Text.RegularExpressions;

namespace Veyra.Desktop.Tests;

public sealed class ArchitectureDependencyRulesTests
{
    private static readonly string RepositoryRoot = ResolveRepositoryRoot();

    private static readonly string[] InfrastructureUsingPatterns =
    [
        "using Veyra.Infrastructure.",
        "using Microsoft.EntityFrameworkCore;",
        "using Microsoft.Data.Sqlite;"
    ];

    private static readonly string[] AllowedDesktopInfrastructureFiles =
    [
        "src/Veyra.Desktop/CompositionRoot/DependencyInjection.cs"
    ];

    [Fact]
    public void Desktop_UiLayer_DoesNotReferenceInfrastructureDirectly()
    {
        var violations = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot, "src", "Veyra.Desktop"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsAllowedDesktopInfrastructureFile(path))
            .SelectMany(FindInfrastructureUsings)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "UI/Desktop files must depend on Application abstractions instead of Infrastructure implementations:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void DesktopTests_DoNotAddNewInfrastructureDataReferencesOutsideKnownIntegrationGate()
    {
        var violations = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot, "tests", "Veyra.Desktop.Tests"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(FindInfrastructureUsings)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Desktop tests should use fakes/mocks and must not add new Infrastructure.Data coupling:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    private static IEnumerable<string> FindInfrastructureUsings(string path)
    {
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (InfrastructureUsingPatterns.Any(pattern => line.Equals(pattern, StringComparison.Ordinal)
                                                           || line.StartsWith(pattern, StringComparison.Ordinal)))
            {
                yield return $"{NormalizeRelative(path)}:{i + 1}: {line}";
            }
        }
    }

    private static bool IsAllowedDesktopInfrastructureFile(string path)
    {
        var relative = NormalizeRelative(path);
        return AllowedDesktopInfrastructureFiles
            .Select(Normalize)
            .Contains(relative, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeRelative(string path)
        => Normalize(Path.GetRelativePath(RepositoryRoot, path));

    private static string Normalize(string path)
        => Regex.Replace(path.Replace('\\', '/'), "/+", "/");

    private static string ResolveRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "VeyraFlow.sln")))
                return current;

            var parent = Directory.GetParent(current);
            if (parent is null)
                break;

            current = parent.FullName;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }
}
