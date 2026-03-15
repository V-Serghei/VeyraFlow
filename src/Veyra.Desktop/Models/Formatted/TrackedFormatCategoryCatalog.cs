using System.Collections.Generic;

namespace Veyra.Desktop.Models.Formatted;

public static class TrackedFormatCategoryCatalog
{
    public static IReadOnlyList<TrackedFormatCategoryDefinition> All { get; } =
    [
        new(
            "documents",
            "format_category.documents",
            [".doc", ".docx", ".pdf", ".txt", ".rtf", ".odt", ".xls", ".xlsx", ".ppt", ".pptx", ".csv"]),
        new(
            "images",
            "format_category.images",
            [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".svg"]),
        new(
            "audio",
            "format_category.audio",
            [".wav", ".mp3", ".aac", ".m4a", ".flac", ".ogg", ".wma", ".aiff"]),
        new(
            "video",
            "format_category.video",
            [".mp4", ".mov", ".avi", ".mkv", ".wmv", ".webm"]),
        new(
            "code",
            "format_category.code",
            [".cs", ".js", ".ts", ".tsx", ".jsx", ".java", ".py", ".go", ".rs", ".cpp", ".h", ".json", ".xml", ".yaml", ".yml", ".md"]),
        new(
            "archives",
            "format_category.archives",
            [".zip", ".7z", ".rar", ".tar", ".gz"])
    ];
}
