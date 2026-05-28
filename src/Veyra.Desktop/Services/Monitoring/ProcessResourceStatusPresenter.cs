using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Monitoring.Models;

namespace Veyra.Desktop.Services.Monitoring;

public static class ProcessResourceStatusPresenter
{
    public static (string AccentColor, string BackgroundColor) DescribeVisuals(ProcessResourceSnapshotDto? snapshot)
    {
        if (snapshot is null)
            return ("#6EA8FF", "#1A6EA8FF");

        var cpu = snapshot.CpuPercent ?? 0;
        if (cpu >= 75 || snapshot.WorkingSetBytes >= 1024L * 1024 * 1024)
            return ("#F97316", "#1AF97316");

        if (cpu >= 40 || snapshot.WorkingSetBytes >= 700L * 1024 * 1024)
            return ("#F59E0B", "#1AF59E0B");

        return ("#4ADE80", "#164ADE80");
    }

    public static string FormatPeakSummary(IReadOnlyList<ProcessResourceHistoryEntryDto> history)
    {
        var peak = SelectPeak(history);
        if (peak is null)
            return Loc.T("process_load.peak_none");

        var timeText = peak.CapturedAtUtc.ToLocalTime().ToString("HH:mm:ss");
        var causeText = FormatCause(peak.Cause);
        return peak.CpuPercent.HasValue
            ? Loc.F(
                "process_load.peak_summary",
                peak.CpuPercent.Value.ToString("0.#", CultureInfo.CurrentCulture),
                FormatSize(peak.WorkingSetBytes),
                causeText,
                timeText)
            : Loc.F(
                "process_load.peak_summary_cpu_pending",
                FormatSize(peak.WorkingSetBytes),
                causeText,
                timeText);
    }

    public static string FormatRecentHistory(IReadOnlyList<ProcessResourceHistoryEntryDto> history, int maxItems = 3)
    {
        if (history.Count == 0)
            return Loc.T("process_load.timeline_none");

        var items = history
            .Where(IsNotable)
            .Take(maxItems)
            .ToList();

        if (items.Count == 0)
            items = history.Take(1).ToList();

        var builder = new StringBuilder();
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (index > 0)
                builder.AppendLine();

            builder.Append(FormatHistoryLine(item));
        }

        return builder.ToString();
    }

    public static string FormatCause(ProcessLoadCauseSnapshotDto cause)
    {
        return cause.Code switch
        {
            "live_sync_fallback" => Loc.F("process_load.cause_live_sync_fallback", cause.Subject ?? Loc.T("common.not_available_short")),
            "live_sync_versions" => Loc.F("process_load.cause_live_sync_versions", cause.Subject ?? Loc.T("common.not_available_short")),
            "live_sync_index" => Loc.F("process_load.cause_live_sync_index", cause.Subject ?? Loc.T("common.not_available_short")),
            "cloud_queue" => Loc.T("process_load.cause_cloud_queue"),
            "cloud_push_all" => Loc.T("process_load.cause_cloud_push_all"),
            "cloud_restore" => Loc.T("process_load.cause_cloud_restore"),
            "cloud_repair" => Loc.T("process_load.cause_cloud_repair"),
            "cloud_refresh" => Loc.T("process_load.cause_cloud_refresh"),
            "cloud_retry" => Loc.T("process_load.cause_cloud_retry"),
            "integrity_check" => Loc.T("process_load.cause_integrity_check"),
            "sync_background" => Loc.T("process_load.cause_sync_background"),
            _ => Loc.T("process_load.cause_background")
        };
    }

    private static string FormatHistoryLine(ProcessResourceHistoryEntryDto item)
    {
        var timeText = item.CapturedAtUtc.ToLocalTime().ToString("HH:mm:ss");
        var causeText = FormatCause(item.Cause);
        return item.CpuPercent.HasValue
            ? Loc.F(
                "process_load.timeline_line",
                timeText,
                item.CpuPercent.Value.ToString("0.#", CultureInfo.CurrentCulture),
                FormatSize(item.WorkingSetBytes),
                causeText)
            : Loc.F(
                "process_load.timeline_line_cpu_pending",
                timeText,
                FormatSize(item.WorkingSetBytes),
                causeText);
    }

    private static ProcessResourceHistoryEntryDto? SelectPeak(IReadOnlyList<ProcessResourceHistoryEntryDto> history)
    {
        return history
            .OrderByDescending(item => item.CpuPercent ?? -1)
            .ThenByDescending(item => item.WorkingSetBytes)
            .ThenByDescending(item => item.CapturedAtUtc)
            .FirstOrDefault();
    }

    private static bool IsNotable(ProcessResourceHistoryEntryDto item)
    {
        if (!string.Equals(item.Cause.Code, "background", StringComparison.Ordinal))
            return true;

        if ((item.CpuPercent ?? 0) >= 20)
            return true;

        return item.WorkingSetBytes >= 500L * 1024 * 1024;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
            return "0 B";

        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var index = 0;

        while (value >= 1024 && index < suffixes.Length - 1)
        {
            value /= 1024d;
            index++;
        }

        var format = value >= 100 || index == 0 ? "0" : "0.#";
        return $"{value.ToString(format, CultureInfo.InvariantCulture)} {suffixes[index]}";
    }
}
