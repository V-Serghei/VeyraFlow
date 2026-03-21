using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.Converters;

public sealed class LocalizedOptionTextConverter : IValueConverter
{
    public static LocalizedOptionTextConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text)
            return value;

        var scope = parameter as string ?? string.Empty;
        var normalized = text.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(normalized))
            return text;

        if (scope == "search.snapshot_tag" && normalized != "all")
            return "#" + normalized;

        var key = scope switch
        {
            "dashboard.availability" => normalized switch
            {
                "all" => "filter.option.all",
                "available" => "filter.option.available",
                "unavailable" => "filter.option.unavailable",
                _ => string.Empty
            },
            "dashboard.sync_state" => normalized switch
            {
                "all" => "filter.option.all",
                "synced" => "filter.option.synced",
                "queued" => "filter.option.queued",
                "syncing" => "filter.option.syncing",
                "retrying" => "filter.option.retrying",
                "conflict" => "filter.option.conflict",
                "auth_required" => "filter.option.auth_required",
                "dead_letter" => "filter.option.dead_letter",
                "failed" => "filter.option.failed",
                _ => string.Empty
            },
            "explorer.entry_type" => normalized switch
            {
                "all" => "filter.option.all",
                "files" => "filter.option.files",
                "folders" => "filter.option.folders",
                _ => string.Empty
            },
            "explorer.modified_window" => normalized switch
            {
                "all" => "filter.option.all",
                "24h" => "filter.option.24h",
                "7d" => "filter.option.7d",
                "30d" => "filter.option.30d",
                _ => string.Empty
            },
            "explorer.snapshot_trigger" => normalized switch
            {
                "all" => "filter.option.all",
                "manual" => "filter.option.manual",
                "scheduled" => "filter.option.scheduled",
                "live_sync" => "filter.option.live_sync",
                "repair" => "filter.option.repair",
                "startup_health_check" => "filter.option.startup_health_check",
                _ => string.Empty
            },
            "explorer.snapshot_change_kind" => normalized switch
            {
                "all" => "filter.option.all",
                "added" => "filter.option.added",
                "modified" => "filter.option.modified",
                "removed" => "filter.option.removed",
                _ => string.Empty
            },
            "search.snapshot_tag" => normalized switch
            {
                "all" => "filter.option.all",
                _ => string.Empty
            },
            "repo.sync_conflict_strategy" => normalized switch
            {
                "last_write_wins" => "filter.option.last_write_wins",
                "manual_merge" => "filter.option.manual_merge",
                "preserve_both" => "filter.option.preserve_both",
                _ => string.Empty
            },
            _ => string.Empty
        };

        if (!string.IsNullOrWhiteSpace(key))
            return Loc.T(key);

        return Humanize(text);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => AvaloniaProperty.UnsetValue;

    private static string Humanize(string value)
    {
        var text = value.Replace('_', ' ').Trim();
        if (string.IsNullOrWhiteSpace(text))
            return value;

        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
