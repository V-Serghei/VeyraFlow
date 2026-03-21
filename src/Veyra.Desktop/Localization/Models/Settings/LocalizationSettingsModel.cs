using System;

namespace Veyra.Desktop.Localization;

internal sealed record LocalizationSettingsModel(
    string? language,
    string? theme,
    string? experience,
    DateTime updatedAtUtc);
