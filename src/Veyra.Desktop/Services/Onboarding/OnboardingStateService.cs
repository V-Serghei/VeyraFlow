using System;

namespace Veyra.Desktop.Services.Onboarding;

public sealed class OnboardingStateService
{
    private bool _pendingFirstRunTour;

    public event EventHandler? FirstRunTourRequested;

    public void RequestFirstRunTour()
    {
        _pendingFirstRunTour = true;
        FirstRunTourRequested?.Invoke(this, EventArgs.Empty);
    }

    public bool ConsumeFirstRunTourRequest()
    {
        if (!_pendingFirstRunTour)
            return false;

        _pendingFirstRunTour = false;
        return true;
    }

    public bool HasPendingTourRequest => _pendingFirstRunTour;
}
