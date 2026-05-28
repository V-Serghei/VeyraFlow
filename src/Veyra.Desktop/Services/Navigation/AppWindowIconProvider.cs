using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Veyra.Desktop.Services.Navigation;

internal static class AppWindowIconProvider
{
    private static WindowIcon? s_icon;

    public static WindowIcon? GetIcon()
    {
        if (s_icon is not null)
            return s_icon;

        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Veyra.Desktop/Assets/veyraFlowLogo.ico"));
            s_icon = new WindowIcon(stream);
            return s_icon;
        }
        catch
        {
            return null;
        }
    }

    public static void Apply(Window window)
    {
        var icon = GetIcon();
        if (icon is not null)
            window.Icon = icon;
    }
}
