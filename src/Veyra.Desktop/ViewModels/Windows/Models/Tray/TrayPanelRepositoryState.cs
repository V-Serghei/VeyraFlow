namespace Veyra.Desktop.ViewModels.Windows;

public sealed class TrayPanelRepositoryState
{
    public TrayPanelRepositoryState(
        string name,
        string path,
        string metricsText,
        string lastSnapshotText,
        string lastSnapshotGlyph,
        string lastSnapshotAccentColor,
        string liveSyncText,
        string liveSyncGlyph,
        string liveSyncAccentColor,
        string liveSyncDetailText,
        string cloudSyncText,
        string cloudSyncGlyph,
        string cloudSyncAccentColor,
        string queueText,
        string queueGlyph,
        string queueAccentColor,
        double progressValue,
        double progressMaximum,
        string progressText,
        bool showProgress,
        string progressAccentColor)
    {
        Name = name;
        Path = path;
        MetricsText = metricsText;
        LastSnapshotText = lastSnapshotText;
        LastSnapshotGlyph = lastSnapshotGlyph;
        LastSnapshotAccentColor = lastSnapshotAccentColor;
        LiveSyncText = liveSyncText;
        LiveSyncGlyph = liveSyncGlyph;
        LiveSyncAccentColor = liveSyncAccentColor;
        LiveSyncDetailText = liveSyncDetailText;
        CloudSyncText = cloudSyncText;
        CloudSyncGlyph = cloudSyncGlyph;
        CloudSyncAccentColor = cloudSyncAccentColor;
        QueueText = queueText;
        QueueGlyph = queueGlyph;
        QueueAccentColor = queueAccentColor;
        ProgressValue = progressValue;
        ProgressMaximum = progressMaximum;
        ProgressText = progressText;
        ShowProgress = showProgress;
        ProgressAccentColor = progressAccentColor;
    }

    public string Name { get; }
    public string Path { get; }
    public string MetricsText { get; }
    public string LastSnapshotText { get; }
    public string LastSnapshotGlyph { get; }
    public string LastSnapshotAccentColor { get; }
    public string LiveSyncText { get; }
    public string LiveSyncGlyph { get; }
    public string LiveSyncAccentColor { get; }
    public string LiveSyncDetailText { get; }
    public string CloudSyncText { get; }
    public string CloudSyncGlyph { get; }
    public string CloudSyncAccentColor { get; }
    public string QueueText { get; }
    public string QueueGlyph { get; }
    public string QueueAccentColor { get; }
    public double ProgressValue { get; }
    public double ProgressMaximum { get; }
    public string ProgressText { get; }
    public bool ShowProgress { get; }
    public string ProgressAccentColor { get; }
}
