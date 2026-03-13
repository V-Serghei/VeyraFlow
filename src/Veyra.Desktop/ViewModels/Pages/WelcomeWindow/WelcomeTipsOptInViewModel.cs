using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veyra.Desktop.Styling;

namespace Veyra.Desktop.ViewModels.Pages.WelcomeWindow;

public sealed record WelcomeTipsSelection(bool EnableTips, string ExperienceModeCode);

public partial class WelcomeTipsOptInViewModel : ObservableObject
{
    private readonly UserExperienceManager _experience = UserExperienceManager.Instance;

    [ObservableProperty]
    private bool _enableTips = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBasicModeSelected))]
    [NotifyPropertyChangedFor(nameof(IsProfessionalModeSelected))]
    private string _selectedExperienceModeCode = UserExperienceManager.Instance.CurrentModeCode;

    public bool IsBasicModeSelected
    {
        get => string.Equals(SelectedExperienceModeCode, "basic", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value)
                SelectedExperienceModeCode = "basic";
        }
    }

    public bool IsProfessionalModeSelected
    {
        get => string.Equals(SelectedExperienceModeCode, "professional", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value)
                SelectedExperienceModeCode = "professional";
        }
    }

    public event Action<WelcomeTipsSelection>? ContinueRequested;

    public WelcomeTipsOptInViewModel()
    {
        SelectedExperienceModeCode = _experience.CurrentModeCode;
    }

    [RelayCommand]
    private void Continue() => ContinueRequested?.Invoke(
        new WelcomeTipsSelection(
            EnableTips,
            string.IsNullOrWhiteSpace(SelectedExperienceModeCode) ? "basic" : SelectedExperienceModeCode));
}
