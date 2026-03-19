using System.Collections.Generic;

namespace Veyra.Desktop.Models.TrackedFormats;

public sealed record TrackedFormatCategoryDefinition(
    string Code,
    string DisplayKey,
    IReadOnlyList<string> Formats);
