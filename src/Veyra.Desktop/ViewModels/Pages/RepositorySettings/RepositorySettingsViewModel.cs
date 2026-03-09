using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Commands.Repository;
using Veyra.Application.DTOs;
using Veyra.Application.Queries;
using Veyra.Application.Queries.Repository;
using Veyra.Desktop.Services.Navigation;

namespace Veyra.Desktop.ViewModels.Pages.RepositorySettings;

public sealed partial class RepositorySettingsViewModel : ObservableObject
{
    private readonly IMediator _mediator;
    private readonly IWindowService _windows;
    private readonly ILogger<RepositorySettingsViewModel> _log;

    private CancellationTokenSource? _retentionCts;

    public event Action? BackRequested;
    public event Func<int, Task>? RepositoryUpdated;
    public event Action? RepositoryDeleted;

    [ObservableProperty] private int _repositoryId;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _repositoryName = string.Empty;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string _directoryPath = string.Empty;
    [ObservableProperty] private string _customFormat = string.Empty;

    [ObservableProperty] private bool _retentionEnabled;
    [ObservableProperty] private string _retentionMaxAgeDays = string.Empty;
    [ObservableProperty] private string _retentionMaxSnapshots = string.Empty;
    [ObservableProperty] private string _retentionMaxTotalSizeMb = string.Empty;
    [ObservableProperty] private string _retentionTriggerFilter = string.Empty;
    [ObservableProperty] private int _retentionRunIntervalMinutes = 60;
    [ObservableProperty] private string _retentionLastRunText = "???????";
    [ObservableProperty] private string _retentionLastStatusText = "-";

    [ObservableProperty] private bool _isRetentionRunning;
    [ObservableProperty] private string _retentionProgressText = string.Empty;
    [ObservableProperty] private string _retentionResultText = string.Empty;

    public ObservableCollection<string> SelectedFormats { get; } = [];
    public ObservableCollection<string> AvailableFormats { get; } = [];

    public RepositorySettingsViewModel(
        IMediator mediator,
        IWindowService windows,
        ILogger<RepositorySettingsViewModel> log)
    {
        _mediator = mediator;
        _windows = windows;
        _log = log;
    }

    public async Task LoadAsync(int repositoryId)
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            var repo = await _mediator.Send(new GetRepositoryDetailQuery(repositoryId));
            if (repo is null)
            {
                ErrorMessage = "??????????? ?? ??????.";
                return;
            }

            var allFormats = await _mediator.Send(new GetTrackedExtensionsQuery());

            RepositoryId = repo.Id;
            RepositoryName = repo.Name;
            Description = repo.Description;
            DirectoryPath = repo.DirectoryPath;

            SelectedFormats.Clear();
            foreach (var format in repo.LinkedFormats.OrderBy(x => x))
                SelectedFormats.Add(format);

            AvailableFormats.Clear();
            foreach (var format in allFormats.OrderBy(x => x))
                AvailableFormats.Add(format);

            ApplyRetentionPolicy(repo.RetentionPolicy);

            RetentionResultText = string.Empty;
            RetentionProgressText = string.Empty;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load repository settings for {RepositoryId}", repositoryId);
            ErrorMessage = "?? ??????? ????????? ????????? ???????????.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task BrowseDirectoryAsync()
    {
        Window? owner = _windows.GetActiveWindow();
        if (owner is null)
            return;

        var res = await owner.StorageProvider.OpenFolderPickerAsync(
            new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "???????? ?????????? ???????????",
                AllowMultiple = false
            });

        var local = res.FirstOrDefault()?.Path.LocalPath;
        if (!string.IsNullOrWhiteSpace(local) && Directory.Exists(local))
            DirectoryPath = local;
    }

    [RelayCommand]
    private void AddAvailableFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return;

        var normalized = NormalizeFormat(format);
        if (SelectedFormats.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            return;

        SelectedFormats.Add(normalized);
    }

    [RelayCommand]
    private void AddCustomFormat()
    {
        var normalized = NormalizeFormat(CustomFormat);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        if (!AvailableFormats.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            AvailableFormats.Add(normalized);

        if (!SelectedFormats.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            SelectedFormats.Add(normalized);

        CustomFormat = string.Empty;
    }

    [RelayCommand]
    private void RemoveFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return;

        var match = SelectedFormats.FirstOrDefault(f =>
            f.Equals(format, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
            SelectedFormats.Remove(match);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            var policy = BuildRetentionPolicyFromState();

            var result = await _mediator.Send(new UpdateRepositoryConfigurationCommand(
                RepositoryId,
                RepositoryName,
                Description,
                DirectoryPath,
                SelectedFormats.ToList(),
                policy));

            if (!result.Success)
            {
                ErrorMessage = result.Error ?? "?? ??????? ????????? ????????? ???????????.";
                return;
            }

            if (RepositoryUpdated is not null)
                await RepositoryUpdated.Invoke(RepositoryId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to save repository settings for {RepositoryId}", RepositoryId);
            ErrorMessage = "?? ??????? ????????? ?????????.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            await _mediator.Send(new DeleteRepositoryCommand(RepositoryId));
            RepositoryDeleted?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to delete repository {RepositoryId}", RepositoryId);
            ErrorMessage = "?? ??????? ??????? ???????????.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunRetention))]
    private Task RunRetentionDryRunAsync() => RunRetentionAsync(dryRun: true);

    [RelayCommand(CanExecute = nameof(CanRunRetention))]
    private Task RunRetentionApplyAsync() => RunRetentionAsync(dryRun: false);

    [RelayCommand(CanExecute = nameof(CanCancelRetention))]
    private void CancelRetention()
    {
        _retentionCts?.Cancel();
    }

    [RelayCommand]
    private void Back()
    {
        BackRequested?.Invoke();
    }

    public bool CanRunRetention => RepositoryId > 0 && RetentionEnabled && !IsRetentionRunning;
    public bool CanCancelRetention => IsRetentionRunning;

    partial void OnIsRetentionRunningChanged(bool value)
    {
        RunRetentionDryRunCommand.NotifyCanExecuteChanged();
        RunRetentionApplyCommand.NotifyCanExecuteChanged();
        CancelRetentionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRunRetention));
        OnPropertyChanged(nameof(CanCancelRetention));
    }

    partial void OnRepositoryIdChanged(int value)
    {
        RunRetentionDryRunCommand.NotifyCanExecuteChanged();
        RunRetentionApplyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRunRetention));
    }

    partial void OnRetentionEnabledChanged(bool value)
    {
        RunRetentionDryRunCommand.NotifyCanExecuteChanged();
        RunRetentionApplyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRunRetention));
    }

    private async Task RunRetentionAsync(bool dryRun)
    {
        if (!CanRunRetention)
            return;

        try
        {
            IsRetentionRunning = true;
            RetentionResultText = string.Empty;
            RetentionProgressText = "??????...";
            ErrorMessage = null;

            _retentionCts?.Dispose();
            _retentionCts = new CancellationTokenSource();

            var progress = new Progress<RepositoryRetentionProgressDto>(p =>
            {
                RetentionProgressText = $"{p.Percent}% {p.Message}";
            });

            var result = await _mediator.Send(
                new RunRepositoryRetentionCommand(RepositoryId, dryRun, progress),
                _retentionCts.Token);

            if (!result.Success || result.Value is null)
            {
                RetentionResultText = result.Error ?? "?? ??????? ????????? ????????.";
                return;
            }

            RetentionResultText = result.Value.Summary;
            RetentionProgressText = "??????.";

            if (!dryRun)
            {
                await LoadAsync(RepositoryId);
            }
        }
        catch (OperationCanceledException)
        {
            RetentionProgressText = "???????? ????????.";
            RetentionResultText = "???????? ???????? ?????????????.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to run retention for repository {RepositoryId}", RepositoryId);
            RetentionResultText = "?????? ?????????? ????????.";
            RetentionProgressText = string.Empty;
        }
        finally
        {
            IsRetentionRunning = false;
            _retentionCts?.Dispose();
            _retentionCts = null;
        }
    }

    private void ApplyRetentionPolicy(RepositoryRetentionPolicyDto policy)
    {
        RetentionEnabled = policy.Enabled;
        RetentionMaxAgeDays = policy.MaxAgeDays?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        RetentionMaxSnapshots = policy.MaxSnapshots?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        RetentionMaxTotalSizeMb = policy.MaxTotalSizeBytes is > 0
            ? Math.Round(policy.MaxTotalSizeBytes.Value / 1024d / 1024d, 2).ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        RetentionTriggerFilter = policy.TriggerFilters.Count == 0
            ? string.Empty
            : string.Join(", ", policy.TriggerFilters);
        RetentionRunIntervalMinutes = Math.Clamp(policy.RunIntervalMinutes, 5, 7 * 24 * 60);
        RetentionLastRunText = policy.LastRunAtUtc is null
            ? "???????"
            : policy.LastRunAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        RetentionLastStatusText = string.IsNullOrWhiteSpace(policy.LastStatus)
            ? "-"
            : policy.LastStatus;
    }

    private RepositoryRetentionPolicyDto BuildRetentionPolicyFromState()
    {
        var maxAgeDays = ParseNullableInt(RetentionMaxAgeDays);
        var maxSnapshots = ParseNullableInt(RetentionMaxSnapshots);
        var maxTotalSizeMb = ParseNullableDouble(RetentionMaxTotalSizeMb);
        long? maxTotalSizeBytes = null;

        if (maxTotalSizeMb is > 0)
            maxTotalSizeBytes = (long)Math.Round(maxTotalSizeMb.Value * 1024d * 1024d, MidpointRounding.AwayFromZero);

        var triggers = RetentionTriggerFilter
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new RepositoryRetentionPolicyDto(
            RetentionEnabled,
            maxAgeDays,
            maxSnapshots,
            maxTotalSizeBytes,
            triggers,
            Math.Clamp(RetentionRunIntervalMinutes, 5, 7 * 24 * 60),
            LastRunAtUtc: null,
            LastStatus: null);
    }

    private static string NormalizeFormat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var v = value.Trim();
        if (!v.StartsWith('.'))
            v = "." + v;

        return v.ToLowerInvariant();
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
}

