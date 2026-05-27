using System.Text.Json;
using System.Text.RegularExpressions;

namespace Veyra.Desktop.Tests;

public sealed class LocalizationCoverageTests
{
    private static readonly Regex CSharpLocRegex = new("Loc\\.(?:T|F|P)\\(\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex XamlLocRegex = new("loc:Tr\\s+([A-Za-z0-9._-]+)", RegexOptions.Compiled);

    [Fact]
    public void LocalizationFiles_ShouldHaveMatchingKeys()
    {
        var root = FindRepositoryRoot();
        var en = LoadKeys(Path.Combine(root, "src", "Veyra.Desktop", "Localization", "en.json"));
        var ru = LoadKeys(Path.Combine(root, "src", "Veyra.Desktop", "Localization", "ru.json"));

        var missingInRu = en.Except(ru, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var missingInEn = ru.Except(en, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(missingInRu.Count == 0, "Missing in ru.json:\n" + string.Join('\n', missingInRu));
        Assert.True(missingInEn.Count == 0, "Missing in en.json:\n" + string.Join('\n', missingInEn));
    }

    [Fact]
    public void ReferencedLocalizationKeys_ShouldExistInBothLanguages()
    {
        var root = FindRepositoryRoot();
        var desktopRoot = Path.Combine(root, "src", "Veyra.Desktop");
        var en = LoadKeys(Path.Combine(desktopRoot, "Localization", "en.json"));
        var ru = LoadKeys(Path.Combine(desktopRoot, "Localization", "ru.json"));

        var missing = new List<string>();

        foreach (var file in Directory.EnumerateFiles(desktopRoot, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(file);
            foreach (Match match in CSharpLocRegex.Matches(content))
            {
                if (!match.Success)
                    continue;

                var key = match.Groups[1].Value;
                if (content[match.Index..].StartsWith($"Loc.P(\"{key}\"", StringComparison.Ordinal))
                {
                    if (!HasPluralOrExactKey(en, key) || !HasPluralOrExactKey(ru, key))
                        missing.Add($"{Path.GetFileName(file)} -> {key}");

                    continue;
                }

                if (!en.Contains(key) || !ru.Contains(key))
                    missing.Add($"{Path.GetFileName(file)} -> {key}");
            }
        }

        foreach (var file in Directory.EnumerateFiles(desktopRoot, "*.axaml", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(file);
            foreach (Match match in XamlLocRegex.Matches(content))
            {
                if (!match.Success)
                    continue;

                var key = match.Groups[1].Value;
                if (!en.Contains(key) || !ru.Contains(key))
                    missing.Add($"{Path.GetFileName(file)} -> {key}");
            }
        }

        var uniqueMissing = missing
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(uniqueMissing.Count == 0, "Missing localization keys:\n" + string.Join('\n', uniqueMissing));
    }

    private static bool HasPluralOrExactKey(HashSet<string> keys, string keyBase)
    {
        return keys.Contains(keyBase)
               || keys.Contains($"{keyBase}.one")
               || keys.Contains($"{keyBase}.few")
               || keys.Contains($"{keyBase}.many")
               || keys.Contains($"{keyBase}.other");
    }

    private static HashSet<string> LoadKeys(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "VeyraFlow.sln")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
