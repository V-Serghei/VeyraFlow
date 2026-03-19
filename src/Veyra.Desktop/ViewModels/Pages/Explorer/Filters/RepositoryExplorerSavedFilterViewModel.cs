using System;
using Veyra.Desktop.Services.State;

namespace Veyra.Desktop.ViewModels.Pages.Explorer;

public sealed class RepositoryExplorerSavedFilterViewModel
{
    public RepositoryExplorerSavedFilterViewModel(RepositoryExplorerFilterPreset preset)
    {
        Preset = preset;
    }

    public RepositoryExplorerFilterPreset Preset { get; }

    public string Name => Preset.Name;

    public DateTime SavedAtUtc => Preset.SavedAtUtc;
}
