using System;
using Veyra.Desktop.Services.State;

namespace Veyra.Desktop.ViewModels.Pages.Search;

public sealed class GlobalSearchSavedFilterViewModel(GlobalSearchFilterPreset preset)
{
    public GlobalSearchFilterPreset Preset { get; } = preset;

    public string Name => Preset.Name;

    public DateTime SavedAtUtc => Preset.SavedAtUtc;
}
