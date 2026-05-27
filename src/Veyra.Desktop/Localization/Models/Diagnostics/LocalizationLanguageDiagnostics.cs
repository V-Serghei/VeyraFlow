using System.Collections.Generic;

namespace Veyra.Desktop.Localization;

public sealed record LocalizationLanguageDiagnostics(
    string LanguageCode,
    int ResourceFileCount,
    int TotalKeysCount,
    int MissingKeysCount,
    int ExtraKeysCount,
    int DuplicateKeysCount,
    IReadOnlyList<string> MissingKeysSample,
    IReadOnlyList<string> DuplicateKeysSample,
    IReadOnlyList<string> ExtraKeysSample)
{
    public static LocalizationLanguageDiagnostics Empty(string languageCode) => new(
        LanguageCode: languageCode,
        ResourceFileCount: 0,
        TotalKeysCount: 0,
        MissingKeysCount: 0,
        ExtraKeysCount: 0,
        DuplicateKeysCount: 0,
        MissingKeysSample: [],
        DuplicateKeysSample: [],
        ExtraKeysSample: []);
}
