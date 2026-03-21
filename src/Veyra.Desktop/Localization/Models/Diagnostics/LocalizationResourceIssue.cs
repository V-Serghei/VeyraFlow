namespace Veyra.Desktop.Localization;

public sealed record LocalizationResourceIssue(string LanguageCode, string FilePath, string? Key, string Message);
