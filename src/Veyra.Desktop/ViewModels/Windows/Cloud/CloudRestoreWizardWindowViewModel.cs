using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.DTOs;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Navigation;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class CloudRestoreWizardWindowViewModel(
    ICloudRepositoryManagementService cloudManager,
    IWindowService windows) : ObservableObject
{
    [ObservableProperty] private int _cloudRepositoryId;
    [ObservableProperty] private string _repositoryName = string.Empty;
    [ObservableProperty] private string? _targetPath;
    [ObservableProperty] private bool _restoreFullHistory;
    [ObservableProperty] private bool _restoreMetadataOnly;
    [ObservableProperty] private bool _relinkExistingLocalFolder;
    [ObservableProperty] private bool _restoreToAnotherFolder;
    [ObservableProperty] private string _planText = Loc.T("cloud_restore.build_plan_first");
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasPlan;
    [ObservableProperty] private bool _canStartRestore;

    public ObservableCollection<CloudRepositoryConflictItemDto> Conflicts { get; } = [];
    public event Action? RequestClose;

    public async Task InitializeAsync(int cloudRepositoryId, string repositoryName, string? suggestedPath)
    {
        CloudRepositoryId = cloudRepositoryId;
        RepositoryName = repositoryName;
        TargetPath = suggestedPath;
        await BuildPlanAsync();
    }

    [RelayCommand]
    private async Task BuildPlanAsync()
    {
        IsBusy = true;
        StatusText = Loc.T("cloud_restore.building_plan");
        try
        {
            var plan = await cloudManager.BuildRestorePlanAsync(BuildOptions());
            HasPlan = true;
            CanStartRestore = true;
            PlanText = Loc.F(
                "cloud_restore.plan_text",
                plan.RepositoryName,
                plan.OriginalCloudPath ?? Loc.T("cloud_restore.unknown_original_path"),
                plan.TargetPath ?? string.Empty,
                plan.WillCreateTargetFolder ? Loc.T("common.yes") : Loc.T("common.no"),
                plan.TargetPathExists ? Loc.T("common.yes") : Loc.T("common.no"),
                plan.WillRelinkExistingFolder ? Loc.T("cloud_restore.relink_existing") : Loc.T("cloud_restore.full_restore"),
                plan.SnapshotCount,
                plan.BlocksAlreadyLocal,
                plan.BlocksToDownload,
                FormatBytes(plan.BytesToDownload),
                plan.WillCreateRecoverySnapshot ? Loc.T("common.yes") : Loc.T("common.no"));
            if (!RestoreToAnotherFolder)
                TargetPath = plan.TargetPath;
            Conflicts.Clear();
            foreach (var conflict in plan.Conflicts)
                Conflicts.Add(conflict);

            StatusText = Loc.T("cloud_restore.plan_ready");
        }
        catch (Exception ex)
        {
            HasPlan = false;
            CanStartRestore = false;
            StatusText = Loc.F("cloud_restore.plan_failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StartRestoreAsync()
    {
        if (!CanStartRestore)
            return;

        IsBusy = true;
        try
        {
            var queued = await cloudManager.QueueRestoreAsync(BuildOptions());
            StatusText = Loc.F("cloud_restore.queued", queued.OperationId);
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            StatusText = Loc.F("cloud_restore.queue_failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Close()
    {
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private async Task BrowseTargetFolderAsync()
    {
        var owner = windows.GetActiveWindow();
        if (owner?.StorageProvider is null)
            return;

        var selection = await owner.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = RestoreToAnotherFolder
                    ? Loc.T("cloud_restore.select_parent_folder")
                    : Loc.T("cloud_restore.select_target_folder"),
                AllowMultiple = false
            });

        var folder = selection.Count > 0 ? selection[0] : null;
        var path = folder?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            TargetPath = path;
    }

    partial void OnRestoreFullHistoryChanged(bool value)
    {
        InvalidatePlan();
    }

    partial void OnRestoreToAnotherFolderChanged(bool value)
    {
        if (value)
            TargetPath = null;

        InvalidatePlan();
    }

    partial void OnRestoreMetadataOnlyChanged(bool value)
    {
        InvalidatePlan();
    }

    partial void OnRelinkExistingLocalFolderChanged(bool value)
    {
        InvalidatePlan();
    }

    private void InvalidatePlan()
    {
        HasPlan = false;
        CanStartRestore = false;
        PlanText = Loc.T("cloud_restore.build_plan_first");
        Conflicts.Clear();
    }

    private CloudRepositoryRestoreOptionsDto BuildOptions()
        => new(
            CloudRepositoryId,
            TargetPath,
            RestoreFullHistory ? "full_history" : "latest",
            RestoreFullHistory,
            RestoreLatestSnapshotOnly: !RestoreFullHistory,
            RestoreMetadataOnly,
            RelinkExistingLocalFolder,
            "preserve_local_with_recovery_snapshot",
            RestoreToAnotherFolder);

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}
