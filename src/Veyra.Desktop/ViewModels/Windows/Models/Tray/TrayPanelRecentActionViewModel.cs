namespace Veyra.Desktop.ViewModels.Windows;

public sealed class TrayPanelRecentActionViewModel
{
    public TrayPanelRecentActionViewModel(string text, string whenText)
    {
        Text = text;
        WhenText = whenText;
    }

    public string Text { get; }
    public string WhenText { get; }
}
