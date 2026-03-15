using System.Collections.Generic;

namespace Veyra.Desktop.Models.Formatted;

public sealed record TrackedFormatCategoryDefinition(
    string Code,
    string DisplayKey,
    IReadOnlyList<string> Formats);
