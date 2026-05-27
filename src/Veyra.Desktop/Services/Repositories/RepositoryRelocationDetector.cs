using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Veyra.Application.DTOs;

namespace Veyra.Desktop.Services.Repositories;

public sealed class RepositoryRelocationDetector : IRepositoryRelocationDetector
{
    private const int MaxSampleFiles = 4;
    private const int MaxCandidateCount = 24;
    private const int MaxVisitedDirectories = 5000;
    private const double AutoRelinkConfidenceThreshold = 0.74d;

    private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$recycle.bin",
        "system volume information",
        "windows",
        "program files",
        "program files (x86)",
        "programdata",
        "appdata",
        "node_modules",
        "bin",
        "obj"
    };

    public Task<RepositoryRelocationSuggestion> SuggestAsync(
        string previousPath,
        IReadOnlyList<RepositoryScanEntryDto> latestEntries,
        CancellationToken ct = default)
    {
        return Task.Run(() => SuggestCore(previousPath, latestEntries, ct), ct);
    }

    private static RepositoryRelocationSuggestion SuggestCore(
        string previousPath,
        IReadOnlyList<RepositoryScanEntryDto> latestEntries,
        CancellationToken ct)
    {
        var normalizedPreviousPath = NormalizePath(previousPath);
        if (string.IsNullOrWhiteSpace(normalizedPreviousPath))
            return RepositoryRelocationSuggestion.None;

        var repositoryFolderName = Path.GetFileName(normalizedPreviousPath);
        if (string.IsNullOrWhiteSpace(repositoryFolderName))
            return RepositoryRelocationSuggestion.None;

        var samples = BuildSamples(latestEntries);
        if (samples.Count == 0)
            return RepositoryRelocationSuggestion.None;

        CandidateScore? best = null;
        CandidateScore? second = null;

        foreach (var candidatePath in EnumerateCandidateDirectories(normalizedPreviousPath, repositoryFolderName, ct))
        {
            var score = ScoreCandidate(candidatePath, normalizedPreviousPath, samples, ct);
            if (score is null)
                continue;

            if (best is null || score.Confidence > best.Confidence)
            {
                second = best;
                best = score;
            }
            else if (second is null || score.Confidence > second.Confidence)
            {
                second = score;
            }
        }

        if (best is null || !best.IsEligible)
        {
            return new RepositoryRelocationSuggestion(
                SuggestedPath: null,
                SampleCount: samples.Count,
                MatchedSampleCount: best?.MatchedSampleCount ?? 0,
                Confidence: 0,
                IsAmbiguous: false);
        }

        var isAmbiguous = second is not null
                          && second.IsEligible
                          && Math.Abs(best.Confidence - second.Confidence) < 0.05d
                          && second.MatchedSampleCount >= best.MatchedSampleCount - 1;

        return new RepositoryRelocationSuggestion(
            best.Path,
            samples.Count,
            best.MatchedSampleCount,
            Math.Round(best.Confidence, 2),
            isAmbiguous);
    }

    private static IReadOnlyList<RepositoryEntrySample> BuildSamples(IReadOnlyList<RepositoryScanEntryDto> latestEntries)
    {
        return latestEntries
            .Where(static entry => !entry.IsDirectory && !string.IsNullOrWhiteSpace(entry.RelativePath))
            .OrderBy(static entry => string.IsNullOrWhiteSpace(entry.ParentRelativePath) ? 0 : 1)
            .ThenBy(static entry => CountPathDepth(entry.RelativePath))
            .ThenByDescending(static entry => entry.SizeBytes > 0)
            .ThenBy(static entry => entry.RelativePath.Length)
            .Select(static entry => new RepositoryEntrySample(entry.RelativePath, entry.SizeBytes))
            .DistinctBy(static sample => sample.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSampleFiles)
            .ToList();
    }

    private static IEnumerable<string> EnumerateCandidateDirectories(
        string previousPath,
        string repositoryFolderName,
        CancellationToken ct)
    {
        var visitedCount = 0;
        var yieldedCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var yieldedBases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var searchWindow in BuildSearchWindows(previousPath))
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(searchWindow.BasePath)
                || !Directory.Exists(searchWindow.BasePath)
                || !yieldedBases.Add(searchWindow.BasePath))
            {
                continue;
            }

            var pending = new Queue<(string Path, int Depth)>();
            pending.Enqueue((searchWindow.BasePath, 0));

            while (pending.Count > 0 && visitedCount < MaxVisitedDirectories && yieldedCandidates.Count < MaxCandidateCount)
            {
                ct.ThrowIfCancellationRequested();
                var (currentPath, depth) = pending.Dequeue();

                foreach (var childDirectory in SafeEnumerateDirectories(currentPath))
                {
                    ct.ThrowIfCancellationRequested();
                    visitedCount++;

                    var normalizedChild = NormalizePath(childDirectory);
                    if (string.IsNullOrWhiteSpace(normalizedChild))
                        continue;

                    var childName = Path.GetFileName(normalizedChild);
                    if (ShouldSkipDirectory(childName))
                        continue;

                    if (childName.Equals(repositoryFolderName, StringComparison.OrdinalIgnoreCase)
                        && !PathEquals(normalizedChild, previousPath)
                        && yieldedCandidates.Add(normalizedChild))
                    {
                        yield return normalizedChild;
                    }

                    if (depth < searchWindow.MaxDepth)
                        pending.Enqueue((normalizedChild, depth + 1));

                    if (visitedCount >= MaxVisitedDirectories || yieldedCandidates.Count >= MaxCandidateCount)
                        yield break;
                }
            }
        }
    }

    private static CandidateScore? ScoreCandidate(
        string candidatePath,
        string previousPath,
        IReadOnlyList<RepositoryEntrySample> samples,
        CancellationToken ct)
    {
        if (samples.Count == 0 || PathEquals(candidatePath, previousPath) || !Directory.Exists(candidatePath))
            return null;

        var matchedSampleCount = 0;
        var sizeMatchedSampleCount = 0;

        foreach (var sample in samples)
        {
            ct.ThrowIfCancellationRequested();

            var expectedPath = Path.Combine(
                candidatePath,
                sample.RelativePath.Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar));

            if (!File.Exists(expectedPath))
                continue;

            matchedSampleCount++;

            try
            {
                var info = new FileInfo(expectedPath);
                if (info.Length == sample.SizeBytes)
                    sizeMatchedSampleCount++;
            }
            catch
            {
            }
        }

        if (matchedSampleCount == 0)
            return null;

        var matchRatio = matchedSampleCount / (double)samples.Count;
        var sizeMatchRatio = matchedSampleCount == 0
            ? 0
            : sizeMatchedSampleCount / (double)matchedSampleCount;
        var confidence = 0.30d + (0.50d * matchRatio) + (0.20d * sizeMatchRatio);
        var minimumMatches = samples.Count <= 1 ? 1 : Math.Min(2, samples.Count);
        var isEligible = matchedSampleCount >= minimumMatches && confidence >= AutoRelinkConfidenceThreshold;

        return new CandidateScore(
            candidatePath,
            matchedSampleCount,
            sizeMatchedSampleCount,
            confidence,
            isEligible);
    }

    private static IEnumerable<SearchWindow> BuildSearchWindows(string previousPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in new[]
                 {
                     CreateSearchWindow(GetAncestorPath(previousPath, 1), 2),
                     CreateSearchWindow(GetAncestorPath(previousPath, 2), 3),
                     CreateSearchWindow(GetAncestorPath(previousPath, 3), 4),
                     CreateSearchWindow(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 5),
                     CreateSearchWindow(Path.GetPathRoot(previousPath), 4)
                 })
        {
            if (candidate is not SearchWindow searchWindow || string.IsNullOrWhiteSpace(searchWindow.BasePath))
                continue;

            if (seen.Add(searchWindow.BasePath))
                yield return searchWindow;
        }
    }

    private static SearchWindow? CreateSearchWindow(string? basePath, int maxDepth)
    {
        var normalized = NormalizePath(basePath);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        return new SearchWindow(normalized, Math.Max(0, maxDepth));
    }

    private static string? GetAncestorPath(string path, int levelsUp)
    {
        var normalized = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var current = new DirectoryInfo(normalized);
        for (var i = 0; i < levelsUp; i++)
        {
            current = current.Parent;
            if (current is null)
                return null;
        }

        return current.FullName;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool ShouldSkipDirectory(string? name)
        => !string.IsNullOrWhiteSpace(name) && SkippedDirectoryNames.Contains(name);

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            return Path.GetFullPath(path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool PathEquals(string left, string right)
        => NormalizePath(left).Equals(NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static int CountPathDepth(string relativePath)
        => relativePath.Count(static c => c is '/' or '\\');

    private sealed record SearchWindow(string BasePath, int MaxDepth);

    private sealed record RepositoryEntrySample(string RelativePath, long SizeBytes);

    private sealed record CandidateScore(
        string Path,
        int MatchedSampleCount,
        int SizeMatchedSampleCount,
        double Confidence,
        bool IsEligible);
}
