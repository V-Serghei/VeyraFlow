namespace Veyra.Desktop.ViewModels.Windows;

public sealed class CloudSyncHealthFilterOptionViewModel
{
    public CloudSyncHealthFilterOptionViewModel(string key, string text)
    {
        Key = key;
        Text = text;
    }

    public string Key { get; }
    public string Text { get; }
}
