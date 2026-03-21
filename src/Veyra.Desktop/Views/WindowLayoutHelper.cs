using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Veyra.Desktop.Views;

internal static class WindowLayoutHelper
{
    public static void FitToWorkingArea(
        Window window,
        Window? owner = null,
        bool maximizeToWorkingArea = false,
        double frameMarginDip = 24d,
        double minWidthDip = 640d,
        double minHeightDip = 480d)
    {
        var screen = ResolveScreen(window, owner);
        if (screen is null)
            return;

        var workingArea = screen.WorkingArea;
        var scaling = screen.Scaling > 0 ? screen.Scaling : 1d;

        var availableWidthDip = System.Math.Max(minWidthDip, (workingArea.Width / scaling) - frameMarginDip);
        var availableHeightDip = System.Math.Max(minHeightDip, (workingArea.Height / scaling) - frameMarginDip);

        if (window.MinWidth > availableWidthDip)
            window.MinWidth = availableWidthDip;
        if (window.MinHeight > availableHeightDip)
            window.MinHeight = availableHeightDip;

        var requestedWidth = maximizeToWorkingArea
            ? availableWidthDip
            : (window.Width > 0 ? window.Width : availableWidthDip);
        var requestedHeight = maximizeToWorkingArea
            ? availableHeightDip
            : (window.Height > 0 ? window.Height : availableHeightDip);

        var targetWidthDip = System.Math.Max(window.MinWidth, System.Math.Min(requestedWidth, availableWidthDip));
        var targetHeightDip = System.Math.Max(window.MinHeight, System.Math.Min(requestedHeight, availableHeightDip));

        window.Width = targetWidthDip;
        window.Height = targetHeightDip;
        window.MaxWidth = double.PositiveInfinity;
        window.MaxHeight = double.PositiveInfinity;

        var targetWidthPx = System.Math.Max(1, (int)System.Math.Round(targetWidthDip * scaling));
        var targetHeightPx = System.Math.Max(1, (int)System.Math.Round(targetHeightDip * scaling));

        var desiredX = workingArea.X + (workingArea.Width - targetWidthPx) / 2;
        var desiredY = workingArea.Y + (workingArea.Height - targetHeightPx) / 2;

        if (owner is not null)
        {
            var ownerWidthDip = owner.Bounds.Width > 0 ? owner.Bounds.Width : owner.Width;
            var ownerHeightDip = owner.Bounds.Height > 0 ? owner.Bounds.Height : owner.Height;
            var ownerWidthPx = System.Math.Max(1, (int)System.Math.Round(ownerWidthDip * scaling));
            var ownerHeightPx = System.Math.Max(1, (int)System.Math.Round(ownerHeightDip * scaling));

            desiredX = owner.Position.X + (ownerWidthPx - targetWidthPx) / 2;
            desiredY = owner.Position.Y + (ownerHeightPx - targetHeightPx) / 2;
        }

        var minX = workingArea.X;
        var minY = workingArea.Y;
        var maxX = workingArea.X + System.Math.Max(0, workingArea.Width - targetWidthPx);
        var maxY = workingArea.Y + System.Math.Max(0, workingArea.Height - targetHeightPx);

        window.Position = new PixelPoint(
            Clamp(desiredX, minX, maxX),
            Clamp(desiredY, minY, maxY));
    }

    private static Screen? ResolveScreen(Window window, Window? owner)
    {
        if (owner is not null)
            return window.Screens.ScreenFromVisual(owner) ?? window.Screens.ScreenFromVisual(window) ?? window.Screens.Primary;

        return window.Screens.ScreenFromVisual(window) ?? window.Screens.Primary;
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
            return min;
        if (value > max)
            return max;
        return value;
    }
}

