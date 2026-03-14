using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Veyra.Desktop.Localization;

public static class UserFacingMessageLocalizer
{
    private static readonly Regex EndpointRegex = new(@"\((?<endpoint>[^)]+)\)\s*$", RegexOptions.Compiled);
    private static readonly Regex UnhandledExceptionRegex = new(@"^Unhandled exception:\s*(?<reason>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TokenExpiredRegex = new(@"^Token expired at\s+(?<at>.+?)\.\s+Sign in again\.$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TokenValidUntilRegex = new(@"^Token valid until\s+(?<at>.+?)\.$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TokenExpiringSoonRegex = new(@"^Token expires soon \((?<minutes>\d+)\s+min left\)\.\s+Consider re-login\.$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TokenPolicySummaryRegex = new(@"^Token warning window:\s*(?<warning>\d+)\s*min\.\s*Allowed clock skew:\s*(?<skew>\d+)\s*sec\.$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex IntegrityAttentionRegex = new(@"^Integrity verification requires attention in\s+(?<problematic>\d+)\s+of\s+(?<total>\d+)\s+repositories\.$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex IntegrityCleanRegex = new(@"^Integrity verification completed with no unresolved issues in\s+(?<total>\d+)\s+repositories\.$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex IntegrityFollowUpRegex = new(@"^(?<problematic>\d+)\/(?<total>\d+)\s+repositories require follow-up\.$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> ExactMessageKeys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["operation started."] = "operation_journal.message.started",
            ["completed successfully."] = "operation_journal.message.completed_success",
            ["completed with warnings."] = "operation_journal.message.completed_warning",
            ["invalid credentials."] = "ui_error.invalid_credentials",
            ["registration failed."] = "ui_error.registration_failed",
            ["username or email is required."] = "ui_error.username_or_email_required",
            ["username or email cannot be whitespace."] = "ui_error.username_or_email_whitespace",
            ["username is required."] = "ui_error.username_required",
            ["username cannot be whitespace."] = "ui_error.username_whitespace",
            ["email is required."] = "ui_error.email_required",
            ["email format is invalid."] = "ui_error.email_invalid",
            ["password is required."] = "ui_error.password_required",
            ["repository name is required."] = "ui_error.repository_name_required",
            ["directory path is required."] = "ui_error.directory_path_required",
            ["bundle path is required."] = "ui_error.bundle_path_required",
            ["target directory path is required."] = "ui_error.target_directory_path_required",
            ["relative path is required."] = "ui_error.relative_path_required",
            ["at least one format is required."] = "ui_error.at_least_one_format_required",
            ["formats cannot contain empty values."] = "ui_error.formats_empty_values",
            ["retention max snapshots must be positive."] = "ui_error.retention_max_snapshots_positive",
            ["sync conflict strategy is invalid."] = "ui_error.sync_strategy_invalid",
            ["repository was not found."] = "ui_error.repository_not_found",
            ["directory was added but repository creation failed."] = "ui_error.directory_added_repository_create_failed",
            ["failed to create repository for directory."] = "ui_error.repository_create_failed",
            ["failed to create repository for the selected directory."] = "ui_error.repository_create_selected_directory_failed",
            ["specified directory does not exist."] = "ui_error.directory_not_found",
            ["directory path does not exist."] = "ui_error.directory_not_found",
            ["new directory does not exist."] = "ui_error.directory_not_found",
            ["path is required."] = "ui_error.path_required",
            ["file path is required."] = "ui_error.file_path_required",
            ["target path is required."] = "ui_error.target_path_required",
            ["key id is required."] = "ui_error.key_id_required",
            ["manifest.json was not found in bundle."] = "ui_error.bundle_manifest_missing",
            ["blocks are missing for selected version."] = "ui_error.version_blocks_missing",
            ["cannot create snapshot: no file changes detected."] = "snapshot.error.no_changes",
            ["artifact encryption is disabled."] = "ui_error.artifact_encryption_disabled",
            ["cloud rejected snapshot push (unauthorized or invalid session)."] = "ui_error.cloud_session_rejected",
            ["cloud rejected follow-up snapshot push after block upload."] = "ui_error.cloud_followup_rejected",
            ["no active token. sign in is required."] = "ui_error.token_missing_sign_in",
            ["token format is invalid. sign in again."] = "ui_error.token_invalid_sign_in",
            ["cloud session expired. sign in again."] = "ui_error.cloud_session_expired",
            ["invalid bearer token"] = "ui_error.cloud_session_expired",
            ["token expired"] = "ui_error.cloud_session_expired"
        };

    public static string LocalizeOrFallback(string? rawMessage, string fallbackKey, params object[] fallbackArgs)
    {
        var localized = TryLocalize(rawMessage);
        return string.IsNullOrWhiteSpace(localized)
            ? Loc.F(fallbackKey, fallbackArgs)
            : localized;
    }

    public static string LocalizeLinesOrFallback(IEnumerable<string> rawMessages, string fallbackKey, params object[] fallbackArgs)
    {
        var messages = rawMessages
            .Select(TryLocalize)
            .Where(static message => !string.IsNullOrWhiteSpace(message))
            .ToArray();

        return messages.Length == 0
            ? Loc.F(fallbackKey, fallbackArgs)
            : string.Join(Environment.NewLine, messages);
    }

    public static string? TryLocalize(string? rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
            return null;

        var lines = rawMessage
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length > 1)
        {
            return string.Join(
                Environment.NewLine,
                lines.Select(LocalizeSingleMessage));
        }

        return LocalizeSingleMessage(lines[0]);
    }

    private static string LocalizeSingleMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return string.Empty;

        if (ContainsCyrillic(message))
            return message.Trim();

        var normalized = Normalize(message);
        if (ExactMessageKeys.TryGetValue(normalized, out var localizationKey))
            return Loc.T(localizationKey);

        var tokenExpiredMatch = TokenExpiredRegex.Match(message);
        if (tokenExpiredMatch.Success)
            return Loc.F("ui_error.token_expired_at", tokenExpiredMatch.Groups["at"].Value.Trim());

        var tokenValidMatch = TokenValidUntilRegex.Match(message);
        if (tokenValidMatch.Success)
            return Loc.F("ui_error.token_valid_until", tokenValidMatch.Groups["at"].Value.Trim());

        var tokenExpiringSoonMatch = TokenExpiringSoonRegex.Match(message);
        if (tokenExpiringSoonMatch.Success)
            return Loc.F("ui_error.token_expiring_soon", tokenExpiringSoonMatch.Groups["minutes"].Value.Trim());

        var tokenPolicySummaryMatch = TokenPolicySummaryRegex.Match(message);
        if (tokenPolicySummaryMatch.Success)
        {
            return Loc.F(
                "ui_error.token_policy_summary",
                tokenPolicySummaryMatch.Groups["warning"].Value.Trim(),
                tokenPolicySummaryMatch.Groups["skew"].Value.Trim());
        }

        var integrityAttentionMatch = IntegrityAttentionRegex.Match(message);
        if (integrityAttentionMatch.Success)
        {
            return Loc.F(
                "operation_journal.message.integrity_attention",
                integrityAttentionMatch.Groups["problematic"].Value.Trim(),
                integrityAttentionMatch.Groups["total"].Value.Trim());
        }

        var integrityCleanMatch = IntegrityCleanRegex.Match(message);
        if (integrityCleanMatch.Success)
            return Loc.F("operation_journal.message.integrity_clean", integrityCleanMatch.Groups["total"].Value.Trim());

        var integrityFollowUpMatch = IntegrityFollowUpRegex.Match(message);
        if (integrityFollowUpMatch.Success)
        {
            return Loc.F(
                "operation_journal.message.integrity_follow_up_details",
                integrityFollowUpMatch.Groups["problematic"].Value.Trim(),
                integrityFollowUpMatch.Groups["total"].Value.Trim());
        }

        var unhandledExceptionMatch = UnhandledExceptionRegex.Match(message);
        if (unhandledExceptionMatch.Success)
        {
            var reason = unhandledExceptionMatch.Groups["reason"].Value.Trim();
            var localizedReason = TryLocalize(reason) ?? reason;
            return Loc.F("operation_journal.message.unhandled_exception", localizedReason);
        }

        if (normalized.Contains("actively refused it", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("connection refused", StringComparison.OrdinalIgnoreCase))
        {
            var endpointMatch = EndpointRegex.Match(message);
            var endpoint = endpointMatch.Success
                ? endpointMatch.Groups["endpoint"].Value.Trim()
                : string.Empty;

            return string.IsNullOrWhiteSpace(endpoint)
                ? Loc.T("ui_error.cloud_offline")
                : Loc.F("ui_error.cloud_offline_at", endpoint);
        }

        if (normalized.Contains("payload is empty", StringComparison.OrdinalIgnoreCase) &&
            normalized.Contains("native", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("ui_error.native_payload_empty");
        }

        if (normalized.Contains("encrypted block envelope", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("artifact key ring has no active key", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("artifact key derivation returned invalid key length", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("ui_error.encrypted_data_invalid");
        }

        if (normalized.Contains("validation failed", StringComparison.OrdinalIgnoreCase))
            return Loc.T("ui_error.validation_failed");

        return message.Trim();
    }

    private static string Normalize(string value)
        => string.Join(" ", value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static bool ContainsCyrillic(string value)
        => value.Any(ch => ch is >= '\u0400' and <= '\u04FF');
}
