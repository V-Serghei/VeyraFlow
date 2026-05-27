using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.Models.TrackedFormats;

public sealed partial class FormatCategoryItemViewModel : ObservableObject
{
    public FormatCategoryItemViewModel(TrackedFormatCategoryDefinition definition)
    {
        Definition = definition;
    }

    public TrackedFormatCategoryDefinition Definition { get; }
    public string Code => Definition.Code;
    public IReadOnlyList<string> Formats => Definition.Formats;
    public string DisplayName => Loc.T(Definition.DisplayKey);

    [ObservableProperty] private bool _isApplied;

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(DisplayName));
    }
}
