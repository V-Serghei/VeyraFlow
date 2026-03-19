using System;
using Veyra.Desktop.Services.State;

namespace Veyra.Desktop.ViewModels.Pages.Dashboard;

public sealed class RepositoryDashboardSavedFilterViewModel
{
    public RepositoryDashboardSavedFilterViewModel(RepositoryDashboardFilterPreset preset)
    {
        Preset = preset;
    }

    public RepositoryDashboardFilterPreset Preset { get; }

    public string Name => Preset.Name;

    public DateTime SavedAtUtc => Preset.SavedAtUtc;
}
