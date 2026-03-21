using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Commands.Repository;
using Veyra.Application.DTOs;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Views.Windows;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class RepositoryRetentionWizardWindowViewModel : ObservableObject
{
    private const string AutomaticTriggerFilter = "automatic";

    private readonly IMediator _mediator;
    private readonly IWindowService _windows;
    private readonly ILogger<RepositoryRetentionWizardWindowViewModel> _log;
    private readonly LocalizationManager _localization = LocalizationManager.Instance;

    public event Action? RequestClose;

    [ObservableProperty] private int _repositoryId;
    [ObservableProperty] private string _repositoryName = string.Empty;
    [ObservableProperty] private int _currentStepIndex;
    [ObservableProperty] private bool _includeAutomatic = true;
    [ObservableProperty] private bool _includeManual;
    [ObservableProperty] private bool _includeWorking;
    [ObservableProperty] private bool _archiveMode;
    [ObservableProperty] private bool _allowManualCleanup;
    [ObservableProperty] private bool _automaticCompactionEnabled;
    [ObservableProperty] private int _selectedAutomaticCompactionWindowHours = 24;
    [ObservableProperty] private string _maxAgeDays = string.Empty;
    [ObservableProperty] private string _maxSnapshots = string.Empty;
    [ObservableProperty] private string _maxTotalSizeMb = string.Empty;
    [ObservableProperty] private int _selectedRunIntervalMinutes = 60;
    [ObservableProperty] private bool _maintenanceWindowEnabled = true;
    [ObservableProperty] private int _selectedMaintenanceWindowStartHour = 1;
    [ObservableProperty] private int _selectedMaintenanceWindowEndHour = 5;
    [ObservableProperty] private RepositoryRetentionRunResultDto? _previewResult;
    [ObservableProperty] private bool _rulesAcknowledged;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _messageText = string.Empty;
    [ObservableProperty] private bool _isSuccessful;

    public RepositoryRetentionPolicyDto? ResultPolicy { get; private set; }

    public ObservableCollection<int> HourOptions { get; } = new(Enumerable.Range(0, 24));
    public ObservableCollection<int> RunIntervalOptions { get; } = [5, 10, 15, 30, 60, 120, 180, 360, 720];
    public ObservableCollection<int> AutomaticCompactionWindowHourOptions { get; } = [6, 12, 24, 48, 72, 168];

    public RepositoryRetentionWizardWindowViewModel(
        IMediator mediator,
        IWindowService windows,
        ILogger<RepositoryRetentionWizardWindowViewModel> log)
    {
        _mediator = mediator;
        _windows = windows;
        _log = log;

        _localization.LanguageChanged += (_, _) => RefreshLocalizationState();
    }

    public string WindowTitleText => Loc.T("repo_settings.retention_wizard_window_title");
    public string TitleText => Loc.T("repo_settings.retention_wizard_title");
    public string SubtitleText => Loc.F("repo_settings.retention_wizard_subtitle", RepositoryName);
    public string StepIndicatorText => Loc.F("repo_settings.retention_wizard_step_indicator", CurrentStepIndex + 1, 3);
    public string StepTitleText => CurrentStepIndex switch
    {
        0 => Loc.T("repo_settings.retention_wizard_step_scope"),
        1 => Loc.T("repo_settings.retention_wizard_step_window"),
        _ => Loc.T("repo_settings.retention_wizard_step_preview")
    };

    public bool IsScopeStepVisible => CurrentStepIndex == 0;
    public bool IsWindowStepVisible => CurrentStepIndex == 1;
    public bool IsPreviewStepVisible => CurrentStepIndex == 2;
    public bool CanGoBack => !IsBusy && CurrentStepIndex > 0;
    public bool CanGoNext => !IsBusy && CurrentStepIndex < 2 && string.IsNullOrWhiteSpace(GetCurrentStepValidationError());
    public bool CanRunPreview => !IsBusy && string.IsNullOrWhiteSpace(ValidateAllStepsBeforePreview());
    public bool CanConfirm => !IsBusy && PreviewResult is not null && RulesAcknowledged;
    public bool HasPreview => PreviewResult is not null;
    public string StorageModeSummaryText => ArchiveMode
        ? Loc.T("repo_settings.retention_storage_mode_archive_summary")
        : Loc.T("repo_settings.retention_storage_mode_delete_summary");
    public string AutomaticCompactionSummaryText =>
        !AutomaticCompactionEnabled
            ? Loc.T("repo_settings.retention_compaction_disabled")
            : Loc.F("repo_settings.retention_compaction_summary", SelectedAutomaticCompactionWindowHours);
    public string MaintenanceWindowSummaryText =>
        !MaintenanceWindowEnabled
            ? Loc.T("repo_settings.retention_window_disabled")
            : SelectedMaintenanceWindowStartHour == SelectedMaintenanceWindowEndHour
                ? Loc.T("repo_settings.retention_window_required")
                : Loc.F(
                    "repo_settings.retention_window_summary",
                    FormatHourLabel(SelectedMaintenanceWindowStartHour),
                    FormatHourLabel(SelectedMaintenanceWindowEndHour));

    public string PreviewStateText
    {
        get
        {
            if (PreviewResult is null)
                return Loc.T("repo_settings.retention_preview_needed");

            return PreviewResult.PolicyApplied
                ? Loc.T("repo_settings.retention_preview_changes_found")
                : Loc.T("repo_settings.retention_preview_no_changes");
        }
    }

    public string PreviewSnapshotsText => (PreviewResult?.SnapshotsMarked ?? 0).ToString(CultureInfo.InvariantCulture);
    public string PreviewAutomaticSnapshotsText => (PreviewResult?.AutomaticSnapshotsMarked ?? 0).ToString(CultureInfo.InvariantCulture);
    public string PreviewAutomaticBreakdownText => FormatRetentionKindBreakdown(PreviewResult?.AutomaticSnapshotsMarked ?? 0);
    public string PreviewManualSnapshotsText => (PreviewResult?.ManualSnapshotsMarked ?? 0).ToString(CultureInfo.InvariantCulture);
    public string PreviewManualBreakdownText => FormatRetentionKindBreakdown(PreviewResult?.ManualSnapshotsMarked ?? 0);
    public string PreviewWorkingSnapshotsText => (PreviewResult?.WorkingSnapshotsMarked ?? 0).ToString(CultureInfo.InvariantCulture);
    public string PreviewWorkingBreakdownText => FormatRetentionKindBreakdown(PreviewResult?.WorkingSnapshotsMarked ?? 0);
    public string PreviewAutomaticCompactionText => (PreviewResult?.AutomaticSnapshotsCompacted ?? 0).ToString(CultureInfo.InvariantCulture);
    public string PreviewVersionsText => (PreviewResult?.FileVersionsMarked ?? 0).ToString(CultureInfo.InvariantCulture);
    public string PreviewDiffsText => (PreviewResult?.DiffsMarked ?? 0).ToString(CultureInfo.InvariantCulture);
    public string PreviewBlocksText => (PreviewResult?.BlockFilesDeleted ?? 0).ToString(CultureInfo.InvariantCulture);
    public string PreviewFreedSpaceText => FormatSize(PreviewResult?.EstimatedFreedBytes ?? 0);
    public string PreviewSummaryText => PreviewResult?.Summary ?? string.Empty;
    public string PreviewImpactText => BuildPreviewImpactText(PreviewResult);
    public string PreviewPrerequisitesText
    {
        get
        {
            if (PreviewResult is null)
                return Loc.T("repo_settings.retention_preview_required");

            if (IncludeManual && !AllowManualCleanup)
                return Loc.T("repo_settings.retention_manual_cleanup_required");

            if (!RulesAcknowledged)
                return Loc.T("repo_settings.retention_ack_required");

            if (IncludeManual && AllowManualCleanup)
                return Loc.T("repo_settings.retention_manual_second_confirm_required");

            return Loc.T("repo_settings.retention_apply_ready");
        }
    }

    public void Configure(int repositoryId, string repositoryName, RepositoryRetentionPolicyDto currentPolicy)
    {
        RepositoryId = repositoryId;
        RepositoryName = repositoryName;
        CurrentStepIndex = 0;

        IncludeAutomatic = currentPolicy.TriggerFilters.Count == 0
                           || currentPolicy.TriggerFilters.Any(static value =>
                               string.Equals(value, "automatic", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase));
        IncludeManual = currentPolicy.TriggerFilters.Any(static value =>
            string.Equals(value, "manual", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase));
        IncludeWorking = currentPolicy.TriggerFilters.Any(static value =>
            string.Equals(value, "working", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase));
        ArchiveMode = RepositoryRetentionStorageModes.IsArchive(currentPolicy.StorageMode);
        AllowManualCleanup = currentPolicy.AllowManualSnapshotCleanup;
        AutomaticCompactionEnabled = currentPolicy.AutomaticCompactionEnabled;
        SelectedAutomaticCompactionWindowHours = currentPolicy.AutomaticCompactionWindowHours is > 0
            ? currentPolicy.AutomaticCompactionWindowHours.Value
            : 24;

        MaxAgeDays = currentPolicy.MaxAgeDays?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        MaxSnapshots = currentPolicy.MaxSnapshots?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        MaxTotalSizeMb = currentPolicy.MaxTotalSizeBytes is > 0
            ? Math.Round(currentPolicy.MaxTotalSizeBytes.Value / 1024d / 1024d, 2).ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        SelectedRunIntervalMinutes = RunIntervalOptions.Contains(currentPolicy.RunIntervalMinutes)
            ? currentPolicy.RunIntervalMinutes
            : Math.Clamp(currentPolicy.RunIntervalMinutes, 5, 720);
        MaintenanceWindowEnabled = currentPolicy.MaintenanceWindowStartHour is not null
            && currentPolicy.MaintenanceWindowEndHour is not null;
        SelectedMaintenanceWindowStartHour = currentPolicy.MaintenanceWindowStartHour ?? 1;
        SelectedMaintenanceWindowEndHour = currentPolicy.MaintenanceWindowEndHour ?? 5;

        PreviewResult = null;
        RulesAcknowledged = false;
        MessageText = string.Empty;
        IsSuccessful = false;
        ResultPolicy = null;

        if (!IncludeAutomatic && !IncludeManual && !IncludeWorking)
            IncludeAutomatic = true;

        if (!MaintenanceWindowEnabled)
            MaintenanceWindowEnabled = true;

        RefreshComputedState();
    }

    [RelayCommand]
    private void Back()
    {
        if (!CanGoBack)
            return;

        CurrentStepIndex--;
        MessageText = string.Empty;
    }

    [RelayCommand]
    private void Next()
    {
        if (CurrentStepIndex >= 2 || IsBusy)
            return;

        var validationError = GetCurrentStepValidationError();
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            MessageText = validationError;
            return;
        }

        CurrentStepIndex++;
        MessageText = string.Empty;
    }

    [RelayCommand]
    private async Task RunPreviewAsync()
    {
        var validationError = ValidateAllStepsBeforePreview();
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            MessageText = validationError;
            return;
        }

        try
        {
            IsBusy = true;
            MessageText = string.Empty;
            PreviewResult = null;

            var result = await _mediator.Send(new RunRepositoryRetentionCommand(
                RepositoryId,
                DryRun: true,
                PolicyOverride: BuildPolicy()));

            if (!result.Success || result.Value is null)
            {
                MessageText = UserFacingMessageLocalizer.LocalizeOrFallback(result.Error, "repo_settings.retention_failed");
                return;
            }

            PreviewResult = result.Value;
            RulesAcknowledged = false;
            MessageText = result.Value.Summary;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Retention setup wizard preview failed. RepositoryId {RepositoryId}", RepositoryId);
            MessageText = Loc.T("repo_settings.retention_failed_exception");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UseSettingsAsync()
    {
        if (!CanConfirm)
        {
            MessageText = PreviewPrerequisitesText;
            return;
        }

        if (!await EnsureManualCleanupConfirmationAsync())
            return;

        ResultPolicy = BuildPolicy();
        IsSuccessful = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        IsSuccessful = false;
        ResultPolicy = null;
        RequestClose?.Invoke();
    }

    partial void OnCurrentStepIndexChanged(int value)
        => RefreshComputedState();

    partial void OnIncludeAutomaticChanged(bool value)
        => OnWizardStateEdited();

    partial void OnIncludeManualChanged(bool value)
        => OnWizardStateEdited();

    partial void OnIncludeWorkingChanged(bool value)
        => OnWizardStateEdited();

    partial void OnArchiveModeChanged(bool value)
        => OnWizardStateEdited();

    partial void OnAllowManualCleanupChanged(bool value)
        => OnWizardStateEdited();

    partial void OnAutomaticCompactionEnabledChanged(bool value)
    {
        if (value && SelectedAutomaticCompactionWindowHours <= 0)
            SelectedAutomaticCompactionWindowHours = 24;

        OnWizardStateEdited();
    }

    partial void OnSelectedAutomaticCompactionWindowHoursChanged(int value)
        => OnWizardStateEdited();

    partial void OnMaxAgeDaysChanged(string value)
        => OnWizardStateEdited();

    partial void OnMaxSnapshotsChanged(string value)
        => OnWizardStateEdited();

    partial void OnMaxTotalSizeMbChanged(string value)
        => OnWizardStateEdited();

    partial void OnSelectedRunIntervalMinutesChanged(int value)
        => OnWizardStateEdited();

    partial void OnMaintenanceWindowEnabledChanged(bool value)
    {
        if (value && SelectedMaintenanceWindowStartHour == SelectedMaintenanceWindowEndHour)
            SelectedMaintenanceWindowEndHour = (SelectedMaintenanceWindowStartHour + 4) % 24;

        OnWizardStateEdited();
    }

    partial void OnSelectedMaintenanceWindowStartHourChanged(int value)
        => OnWizardStateEdited();

    partial void OnSelectedMaintenanceWindowEndHourChanged(int value)
        => OnWizardStateEdited();

    partial void OnPreviewResultChanged(RepositoryRetentionRunResultDto? value)
        => RefreshComputedState();

    partial void OnRulesAcknowledgedChanged(bool value)
        => RefreshComputedState();

    partial void OnIsBusyChanged(bool value)
        => RefreshComputedState();

    private void OnWizardStateEdited()
    {
        PreviewResult = null;
        RulesAcknowledged = false;
        RefreshComputedState();
    }

    private void RefreshLocalizationState()
    {
        OnPropertyChanged(nameof(WindowTitleText));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(StepIndicatorText));
        OnPropertyChanged(nameof(StepTitleText));
        OnPropertyChanged(nameof(AutomaticCompactionSummaryText));
        OnPropertyChanged(nameof(MaintenanceWindowSummaryText));
        OnPropertyChanged(nameof(PreviewStateText));
        OnPropertyChanged(nameof(PreviewSnapshotsText));
        OnPropertyChanged(nameof(PreviewAutomaticSnapshotsText));
        OnPropertyChanged(nameof(PreviewAutomaticBreakdownText));
        OnPropertyChanged(nameof(PreviewManualSnapshotsText));
        OnPropertyChanged(nameof(PreviewManualBreakdownText));
        OnPropertyChanged(nameof(PreviewWorkingSnapshotsText));
        OnPropertyChanged(nameof(PreviewWorkingBreakdownText));
        OnPropertyChanged(nameof(PreviewAutomaticCompactionText));
        OnPropertyChanged(nameof(PreviewVersionsText));
        OnPropertyChanged(nameof(PreviewDiffsText));
        OnPropertyChanged(nameof(PreviewBlocksText));
        OnPropertyChanged(nameof(PreviewFreedSpaceText));
        OnPropertyChanged(nameof(PreviewSummaryText));
        OnPropertyChanged(nameof(PreviewImpactText));
        OnPropertyChanged(nameof(PreviewPrerequisitesText));
    }

    private void RefreshComputedState()
    {
        OnPropertyChanged(nameof(IsScopeStepVisible));
        OnPropertyChanged(nameof(IsWindowStepVisible));
        OnPropertyChanged(nameof(IsPreviewStepVisible));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanRunPreview));
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(StorageModeSummaryText));
        OnPropertyChanged(nameof(AutomaticCompactionSummaryText));
        OnPropertyChanged(nameof(MaintenanceWindowSummaryText));
        OnPropertyChanged(nameof(PreviewStateText));
        OnPropertyChanged(nameof(PreviewSnapshotsText));
        OnPropertyChanged(nameof(PreviewAutomaticSnapshotsText));
        OnPropertyChanged(nameof(PreviewAutomaticBreakdownText));
        OnPropertyChanged(nameof(PreviewManualSnapshotsText));
        OnPropertyChanged(nameof(PreviewManualBreakdownText));
        OnPropertyChanged(nameof(PreviewWorkingSnapshotsText));
        OnPropertyChanged(nameof(PreviewWorkingBreakdownText));
        OnPropertyChanged(nameof(PreviewAutomaticCompactionText));
        OnPropertyChanged(nameof(PreviewVersionsText));
        OnPropertyChanged(nameof(PreviewDiffsText));
        OnPropertyChanged(nameof(PreviewBlocksText));
        OnPropertyChanged(nameof(PreviewFreedSpaceText));
        OnPropertyChanged(nameof(PreviewSummaryText));
        OnPropertyChanged(nameof(PreviewImpactText));
        OnPropertyChanged(nameof(PreviewPrerequisitesText));
        OnPropertyChanged(nameof(StepIndicatorText));
        OnPropertyChanged(nameof(StepTitleText));
    }

    private string? GetCurrentStepValidationError()
        => CurrentStepIndex switch
        {
            0 => ValidateScopeStep(),
            1 => ValidateWindowStep(),
            _ => null
        };

    private string? ValidateAllStepsBeforePreview()
        => ValidateScopeStep() ?? ValidateWindowStep();

    private string? ValidateScopeStep()
    {
        if (!IncludeAutomatic && !IncludeManual && !IncludeWorking)
            return Loc.T("repo_settings.retention_wizard_error_scope_required");

        if (IncludeManual && !AllowManualCleanup)
            return Loc.T("repo_settings.retention_manual_cleanup_required");

        if (AutomaticCompactionEnabled && SelectedAutomaticCompactionWindowHours <= 0)
            return Loc.T("repo_settings.retention_compaction_window_required");

        var hasAnyRule = ParseNullableInt(MaxAgeDays) is not null
                         || ParseNullableInt(MaxSnapshots) is not null
                         || ParseNullableDouble(MaxTotalSizeMb) is not null;

        return hasAnyRule
            ? null
            : Loc.T("repo_settings.retention_wizard_error_rule_required");
    }

    private string? ValidateWindowStep()
    {
        if (!MaintenanceWindowEnabled)
            return Loc.T("repo_settings.retention_window_required");

        if (SelectedMaintenanceWindowStartHour == SelectedMaintenanceWindowEndHour)
            return Loc.T("repo_settings.retention_window_required");

        return null;
    }

    private async Task<bool> EnsureManualCleanupConfirmationAsync()
    {
        if (!(IncludeManual && AllowManualCleanup))
            return true;

        var owner = _windows.GetActiveWindow();
        if (owner is null)
            return false;

        var window = _windows.Create<ConfirmActionWindow>();
        if (window.DataContext is ConfirmActionWindowViewModel vm)
        {
            vm.ConfigureLocalized(
                "repo_settings.retention_manual_second_confirm_title",
                "repo_settings.retention_manual_second_confirm_configure_body",
                [RepositoryName],
                "repo_settings.retention_manual_second_confirm_warning",
                "repo_settings.retention_manual_second_confirm_configure_button");
        }

        await _windows.ShowDialogAsync(window, owner);
        return window.DataContext is ConfirmActionWindowViewModel resultVm && resultVm.IsConfirmed;
    }

    private RepositoryRetentionPolicyDto BuildPolicy()
    {
        var maxAgeDays = ParseNullableInt(MaxAgeDays);
        var maxSnapshots = ParseNullableInt(MaxSnapshots);
        var maxTotalSizeMb = ParseNullableDouble(MaxTotalSizeMb);
        long? maxTotalSizeBytes = null;

        if (maxTotalSizeMb is > 0)
            maxTotalSizeBytes = (long)Math.Round(maxTotalSizeMb.Value * 1024d * 1024d, MidpointRounding.AwayFromZero);

        var triggers = Enumerable.Empty<string>()
            .Concat(IncludeAutomatic ? [AutomaticTriggerFilter] : [])
            .Concat(IncludeManual ? ["manual"] : [])
            .Concat(IncludeWorking ? ["working"] : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (triggers.Count == 0)
            triggers = [AutomaticTriggerFilter];

        return new RepositoryRetentionPolicyDto(
            Enabled: true,
            MaxAgeDays: maxAgeDays,
            MaxSnapshots: maxSnapshots,
            MaxTotalSizeBytes: maxTotalSizeBytes,
            TriggerFilters: triggers,
            RunIntervalMinutes: Math.Clamp(SelectedRunIntervalMinutes, 5, 7 * 24 * 60),
            MaintenanceWindowStartHour: MaintenanceWindowEnabled ? SelectedMaintenanceWindowStartHour : null,
            MaintenanceWindowEndHour: MaintenanceWindowEnabled ? SelectedMaintenanceWindowEndHour : null,
            LastRunAtUtc: null,
            LastStatus: null,
            StorageMode: ArchiveMode ? RepositoryRetentionStorageModes.Archive : RepositoryRetentionStorageModes.Delete,
            AllowManualSnapshotCleanup: AllowManualCleanup,
            AutomaticCompactionEnabled: AutomaticCompactionEnabled,
            AutomaticCompactionWindowHours: AutomaticCompactionEnabled
                ? SelectedAutomaticCompactionWindowHours
                : null);
    }

    private static int? ParseNullableInt(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0
            ? parsed
            : null;
    }

    private static double? ParseNullableDouble(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0
            ? parsed
            : null;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    private string FormatRetentionKindBreakdown(int count)
    {
        var total = PreviewResult?.SnapshotsMarked ?? 0;
        if (total <= 0 || count <= 0)
            return Loc.T("repo_settings.retention_preview_share_zero");

        return Loc.F("repo_settings.retention_preview_share_format", FormatSnapshotShare(count, total));
    }

    private string BuildPreviewImpactText(RepositoryRetentionRunResultDto? preview)
    {
        if (preview is null)
            return Loc.T("repo_settings.retention_preview_impact_needed");

        if (!preview.PolicyApplied || preview.SnapshotsMarked <= 0)
            return Loc.T("repo_settings.retention_preview_impact_none");

        if (preview.ArchiveMode)
        {
            if (preview.ManualSnapshotsMarked > 0)
            {
                return Loc.F(
                    "repo_settings.retention_preview_impact_archive_with_manual",
                    preview.SnapshotsArchived > 0 ? preview.SnapshotsArchived : preview.SnapshotsMarked,
                    preview.ManualSnapshotsMarked);
            }

            return Loc.F(
                "repo_settings.retention_preview_impact_archive_only",
                preview.SnapshotsArchived > 0 ? preview.SnapshotsArchived : preview.SnapshotsMarked);
        }

        var automatic = preview.AutomaticSnapshotsMarked;
        var manual = preview.ManualSnapshotsMarked;
        var working = preview.WorkingSnapshotsMarked;

        if (manual > 0 && automatic == 0 && working == 0)
            return AppendAutomaticCompactionImpact(
                Loc.F("repo_settings.retention_preview_impact_manual_only", manual),
                preview.AutomaticSnapshotsCompacted);

        if (manual > 0)
            return AppendAutomaticCompactionImpact(
                Loc.F("repo_settings.retention_preview_impact_includes_manual", manual),
                preview.AutomaticSnapshotsCompacted);

        if (automatic > 0 && working > 0)
            return AppendAutomaticCompactionImpact(
                Loc.F("repo_settings.retention_preview_impact_auto_and_working_only", automatic, working),
                preview.AutomaticSnapshotsCompacted);

        if (automatic > 0)
            return AppendAutomaticCompactionImpact(
                Loc.F("repo_settings.retention_preview_impact_automatic_only", automatic),
                preview.AutomaticSnapshotsCompacted);

        if (working > 0)
            return AppendAutomaticCompactionImpact(
                Loc.F("repo_settings.retention_preview_impact_working_only", working),
                preview.AutomaticSnapshotsCompacted);

        return Loc.T("repo_settings.retention_preview_impact_none");
    }

    private string AppendAutomaticCompactionImpact(string baseImpact, int compactedCount)
    {
        if (compactedCount <= 0)
            return baseImpact;

        return $"{baseImpact} {Loc.F("repo_settings.retention_preview_impact_compaction_suffix", compactedCount)}";
    }

    private static string FormatSnapshotShare(int count, int total)
    {
        if (total <= 0 || count <= 0)
            return "0%";

        var percentage = count * 100d / total;
        return percentage >= 10d || Math.Abs(percentage % 1d) < 0.05d
            ? percentage.ToString("0", CultureInfo.InvariantCulture) + "%"
            : percentage.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }

    private static string FormatHourLabel(int hour)
        => $"{Math.Clamp(hour, 0, 23):00}:00";
}
