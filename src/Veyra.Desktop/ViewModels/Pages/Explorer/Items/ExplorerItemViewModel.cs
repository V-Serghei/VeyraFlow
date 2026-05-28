using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Veyra.Desktop.ViewModels.Pages.Explorer;

public sealed partial class ExplorerItemViewModel : ObservableObject
{
    public string RelativePath { get; init; } = string.Empty;
    public string? ParentRelativePath { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsTracked { get; init; } = true;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _type = string.Empty;
    [ObservableProperty] private string _sizeDisplay = "-";
    [ObservableProperty] private string _modifiedDisplay = string.Empty;
    [ObservableProperty] private string? _hashSha256;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    [NotifyPropertyChangedFor(nameof(IsStatusSlash))]
    [NotifyPropertyChangedFor(nameof(IsStatusCheck))]
    private string _statusText = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusSlash))]
    [NotifyPropertyChangedFor(nameof(IsStatusCheck))]
    private string _statusKind = "tracked";
    [ObservableProperty] private string _statusBackground = "#193B68";
    [ObservableProperty] private string _statusForeground = "#D7E7FF";
    [ObservableProperty] private string _statusGlyph = "\u2713";
    [ObservableProperty] private string _tooltipText = string.Empty;
    [ObservableProperty] private double _rowOpacity = 1d;

    public string IconGlyph => ResolveIcon().Glyph;
    public string IconLabel => ResolveIcon().Label;
    public string IconBackground => ResolveIcon().Background;
    public string IconForeground => ResolveIcon().Foreground;
    public bool HasIconLabel => !string.IsNullOrWhiteSpace(IconLabel);
    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);
    public bool IsStatusSlash => StatusKind is "untracked" or "ignored" or "deleted";
    public bool IsStatusCheck => HasStatusText && !IsStatusSlash;
    public bool CanOpenDirectory => IsDirectory;

    private FileIconVisual ResolveIcon()
    {
        if (IsDirectory)
            return new("\uE8B7", string.Empty, "#193B68", "#6EA8FF");

        var extension = Path.GetExtension(RelativePath).Trim().TrimStart('.').ToUpperInvariant();
        var normalized = extension.ToLowerInvariant();

        if (IsImage(normalized))
            return new("\uEB9F", string.Empty, "#173D36", "#5EE0B5");
        if (IsAudio(normalized))
            return new("\uE8D6", string.Empty, "#3A2B11", "#F6C15E");
        if (IsVideo(normalized))
            return new("\uE714", string.Empty, "#3A2235", "#F08AD6");
        if (IsCode(normalized))
            return new("\uE943", string.Empty, "#1D3158", "#78A8FF");
        if (IsArchive(normalized))
            return new("\uE7B8", string.Empty, "#372B13", "#D9B45D");
        if (IsDocument(normalized))
            return new("\uE8A5", extension, "#1C3446", "#8FD3FF");

        return new("\uE8A5", extension.Length == 0 ? "FILE" : extension, "#17212E", "#D7E7FF");
    }

    private static bool IsImage(string extension)
        => extension is "png" or "jpg" or "jpeg" or "gif" or "bmp" or "webp" or "tif" or "tiff" or "svg" or "ico";

    private static bool IsAudio(string extension)
        => extension is "mp3" or "wav" or "flac" or "aac" or "ogg" or "m4a" or "wma" or "aiff";

    private static bool IsVideo(string extension)
        => extension is "mp4" or "mov" or "avi" or "mkv" or "webm" or "wmv" or "mpeg" or "mpg";

    private static bool IsCode(string extension)
        => extension is "cs" or "cpp" or "c" or "h" or "java" or "js" or "jsx" or "ts" or "tsx" or "json" or "xml" or "html" or "css" or "scss" or "py" or "go" or "rs" or "php" or "sql" or "sh" or "ps1" or "bat" or "cmd";

    private static bool IsArchive(string extension)
        => extension is "zip" or "7z" or "rar" or "gz" or "tar" or "tgz" or "bz2" or "xz";

    private static bool IsDocument(string extension)
        => extension is "txt" or "md" or "doc" or "docx" or "pdf" or "xls" or "xlsx" or "ppt" or "pptx" or "csv" or "rtf";

    private readonly record struct FileIconVisual(
        string Glyph,
        string Label,
        string Background,
        string Foreground);
}
