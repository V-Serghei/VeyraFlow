using System;
using Veyra.Application.DTOs;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.ViewModels;

internal static class RepositoryCreationProgressText
{
    public static string Format(RepositoryCreationProgressDto progress)
    {
        var stage = (progress.Stage ?? string.Empty).Trim().ToLowerInvariant();
        var processed = Math.Max(0, progress.FilesProcessed);
        var total = Math.Max(0, progress.FilesTotal);
        var visibleTotal = Math.Max(processed, total);

        return stage switch
        {
            "prepare" => Loc.T("create_repo.progress_prepare"),
            "formats" => Loc.T("create_repo.progress_formats"),
            "scan" => visibleTotal > 0
                ? Loc.F("create_repo.progress_scan_tracked_files", visibleTotal)
                : Loc.T("create_repo.progress_scan"),
            "save" => visibleTotal > 0
                ? Loc.F("create_repo.progress_save_tracked_files", visibleTotal)
                : Loc.T("create_repo.progress_save"),
            "save_versions" => total > 0
                ? Loc.F("create_repo.progress_save_versions", processed, total)
                : Loc.T("create_repo.progress_save_versions_unknown"),
            "save_persist_versions" => Loc.T("create_repo.progress_persist_versions"),
            "save_links" => Loc.T("create_repo.progress_link_versions"),
            "save_diff_precompute" => Loc.T("create_repo.progress_prepare_diffs"),
            "sync" => Loc.T("create_repo.progress_apply_configuration"),
            "done" => TryFormatDone(progress.Message),
            _ => UserFacingMessageLocalizer.TryLocalize(progress.Message)
                 ?? progress.Message?.Trim()
                 ?? Loc.T("create_repo.progress_working")
        };
    }

    private static string TryFormatDone(string? message)
    {
        var normalized = (message ?? string.Empty).Trim();
        if (normalized.Contains("warnings", StringComparison.OrdinalIgnoreCase)
            && normalized.Contains("file", StringComparison.OrdinalIgnoreCase))
        {
            var count = ExtractFirstInteger(normalized);
            return count > 0
                ? Loc.F("create_repo.progress_done_with_busy_files", count)
                : Loc.T("create_repo.progress_done_with_warnings");
        }

        return Loc.T("create_repo.success_progress_label");
    }

    private static int ExtractFirstInteger(string value)
    {
        var digits = string.Empty;
        foreach (var ch in value)
        {
            if (char.IsDigit(ch))
            {
                digits += ch;
                continue;
            }

            if (digits.Length > 0)
                break;
        }

        return int.TryParse(digits, out var result) ? result : 0;
    }
}
