using System.Collections.Generic;
using Veyra.Application.Common.Files;

namespace Veyra.Desktop.Models.TrackedFormats;

public static class TrackedFormatCategoryCatalog
{
    public static IReadOnlyList<TrackedFormatCategoryDefinition> All { get; } =
    [
        new(
            "documents",
            "format_category.documents",
            KnownFileExtensions.TrackedDocumentFormats),
        new(
            "images",
            "format_category.images",
            KnownFileExtensions.TrackedImageFormats),
        new(
            "audio",
            "format_category.audio",
            KnownFileExtensions.TrackedAudioFormats),
        new(
            "video",
            "format_category.video",
            KnownFileExtensions.TrackedVideoFormats),
        new(
            "code",
            "format_category.code",
            KnownFileExtensions.TrackedCodeFormats),
        new(
            "archives",
            "format_category.archives",
            KnownFileExtensions.TrackedArchiveFormats)
    ];
}
