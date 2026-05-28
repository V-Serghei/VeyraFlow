using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Veyra.Desktop.ViewModels.Pages.Dashboard;

public sealed partial class DashboardFolderTreeNodeViewModel : ObservableObject
{
    public DashboardFolderTreeNodeViewModel(
        string name,
        string fullPath,
        bool isDrive,
        bool isTracked,
        bool hasTrackedDescendant,
        bool isInsideTrackedRepository,
        RepositoryCardViewModel? repository = null,
        bool isPlaceholder = false)
    {
        Name = name;
        FullPath = fullPath;
        IsDrive = isDrive;
        IsTracked = isTracked;
        HasTrackedDescendant = hasTrackedDescendant;
        IsInsideTrackedRepository = isInsideTrackedRepository;
        Repository = repository;
        IsPlaceholder = isPlaceholder;
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDrive { get; }
    public bool IsTracked { get; }
    public bool HasTrackedDescendant { get; }
    public bool IsInsideTrackedRepository { get; }
    public RepositoryCardViewModel? Repository { get; }
    public bool IsPlaceholder { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isLoaded;

    public ObservableCollection<DashboardFolderTreeNodeViewModel> Children { get; } = [];

    public bool HasRepository => Repository is not null;
    public bool CanCreateRepository => !IsPlaceholder && !IsTracked && !string.IsNullOrWhiteSpace(FullPath);
    public bool CanOpenRepositoryActions => Repository is not null;
    public string Icon => IsDrive ? "\uE8B7" : "\uE8B7";
    public string AccentColor => IsTracked
        ? "#2FB344"
        : IsInsideTrackedRepository
            ? "#86EFAC"
        : HasTrackedDescendant
            ? "#6EA8FF"
            : "#64748B";

    public string StatusHint => IsTracked
        ? "Tracked repository"
        : IsInsideTrackedRepository
            ? "Covered by parent repository"
        : HasTrackedDescendant
            ? "Tracked repository below"
            : "No active repository";

    public string TooltipTitle => Repository?.Name ?? Name;
    public string TooltipPath => FullPath;
    public string TooltipSize => Repository?.SizeDisplay ?? string.Empty;
    public string TooltipLastActivity => Repository?.LastActivity ?? string.Empty;
    public string TooltipSync => Repository?.CloudSyncStatus ?? string.Empty;
    public string TooltipFormats => Repository?.FormatsBadge ?? string.Empty;
}
