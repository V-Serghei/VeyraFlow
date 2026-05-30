namespace Veyra.Application.Common.Files;

public static class KnownFileExtensions
{
    public const string ExtensionlessFileFormat = ".file";

    public static IReadOnlyList<string> TrackedDocumentFormats { get; } =
    [
        ".doc", ".docx", ".pdf", ".txt", ".rtf", ".odt", ".xls", ".xlsx", ".ppt", ".pptx", ".csv"
    ];

    public static IReadOnlyList<string> TrackedImageFormats { get; } =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".svg"
    ];

    public static IReadOnlyList<string> TrackedAudioFormats { get; } =
    [
        ".wav", ".mp3", ".aac", ".m4a", ".flac", ".ogg", ".wma", ".aiff"
    ];

    public static IReadOnlyList<string> TrackedVideoFormats { get; } =
    [
        ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".webm"
    ];

    public static IReadOnlyList<string> TrackedCodeFormats { get; } =
    [
        ".cs", ".js", ".ts", ".tsx", ".jsx", ".java", ".py", ".go", ".rs", ".cpp", ".h", ".json", ".xml", ".yaml", ".yml", ".md"
    ];

    public static IReadOnlyList<string> TrackedArchiveFormats { get; } =
    [
        ".zip", ".7z", ".rar", ".tar", ".gz"
    ];

    private static IReadOnlyList<string> TextDiffFormats { get; } =
    [
        ".txt", ".md", ".csv", ".json", ".xml", ".yml", ".yaml", ".ini", ".toml", ".log",
        ".cs", ".js", ".ts", ".tsx", ".jsx", ".java", ".py", ".rs", ".go", ".c", ".cpp", ".h", ".hpp",
        ".html", ".css", ".sql", ".xaml", ".axaml"
    ];

    private static IReadOnlyList<string> TextContentFormats { get; } =
    [
        ".txt", ".md", ".csv", ".json", ".xml", ".yml", ".yaml", ".ini", ".toml", ".log",
        ".cs", ".js", ".ts", ".tsx", ".jsx", ".java", ".py", ".rs", ".go", ".c", ".cpp", ".h", ".hpp",
        ".html", ".css", ".sql", ".xaml", ".axaml", ".svg"
    ];

    private static IReadOnlyList<string> ImageDiffFormats { get; } =
    [
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".svg"
    ];

    private static IReadOnlyList<string> AudioDiffFormats { get; } =
    [
        ".wav", ".mp3", ".aac", ".m4a", ".wma", ".aif", ".aiff"
    ];

    private static IReadOnlyList<string> ArchiveDiffFormats { get; } =
    [
        ".zip"
    ];

    private static IReadOnlyList<string> OfficeBinaryHintFormats { get; } =
    [
        ".doc", ".docx", ".rtf", ".odt"
    ];

    private static readonly HashSet<string> TextDiffSet = new(TextDiffFormats, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> TextContentSet = new(TextContentFormats, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ImageDiffSet = new(ImageDiffFormats, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AudioDiffSet = new(AudioDiffFormats, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ArchiveDiffSet = new(ArchiveDiffFormats, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> OfficeBinaryHintSet = new(OfficeBinaryHintFormats, StringComparer.OrdinalIgnoreCase);

    public static bool IsTextDiffExtension(string? extension)
        => NormalizeExtension(extension) is { } normalized && TextDiffSet.Contains(normalized);

    public static bool IsTextContentExtension(string? extension)
        => NormalizeExtension(extension) is { } normalized && TextContentSet.Contains(normalized);

    public static bool IsImageDiffExtension(string? extension)
        => NormalizeExtension(extension) is { } normalized && ImageDiffSet.Contains(normalized);

    public static bool IsAudioDiffExtension(string? extension)
        => NormalizeExtension(extension) is { } normalized && AudioDiffSet.Contains(normalized);

    public static bool IsArchiveDiffExtension(string? extension)
        => NormalizeExtension(extension) is { } normalized && ArchiveDiffSet.Contains(normalized);

    public static bool IsOfficeBinaryHintExtension(string? extension)
        => NormalizeExtension(extension) is { } normalized && OfficeBinaryHintSet.Contains(normalized);

    public static string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return null;

        var normalized = extension.Trim();
        if (!normalized.StartsWith('.'))
            normalized = "." + normalized;

        return normalized.ToLowerInvariant();
    }

    public static string NormalizeTrackedFileFormat(string? extension)
        => NormalizeExtension(extension) ?? ExtensionlessFileFormat;

    public static bool IsExtensionlessFileFormat(string? extension)
        => string.Equals(NormalizeExtension(extension), ExtensionlessFileFormat, StringComparison.OrdinalIgnoreCase);
}
