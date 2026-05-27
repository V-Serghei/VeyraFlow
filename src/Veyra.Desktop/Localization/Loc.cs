using System;
using System.Globalization;

namespace Veyra.Desktop.Localization;

public static class Loc
{
    public static string T(string key) => LocalizationManager.Instance.Get(key);

    public static string F(string key, params object[] args)
    {
        var template = T(key);
        return args is { Length: > 0 }
            ? string.Format(CultureInfo.CurrentCulture, template, args)
            : template;
    }

    public static string P(string keyBase, int count, params object[] args)
    {
        var category = ResolvePluralCategory(LocalizationManager.Instance.CurrentLanguageCode, count);

        var categoryKey = $"{keyBase}.{category}";
        var fallbackKey = $"{keyBase}.other";

        var template = T(categoryKey);
        if (string.Equals(template, categoryKey, StringComparison.Ordinal))
            template = T(fallbackKey);

        if (string.Equals(template, fallbackKey, StringComparison.Ordinal))
            template = T(keyBase);

        return args is { Length: > 0 }
            ? string.Format(CultureInfo.CurrentCulture, template, args)
            : template;
    }

    private static string ResolvePluralCategory(string languageCode, int count)
    {
        var n = Math.Abs(count);
        var mod10 = n % 10;
        var mod100 = n % 100;

        return languageCode switch
        {
            "ru" => mod10 == 1 && mod100 != 11
                ? "one"
                : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14
                    ? "few"
                    : mod10 == 0 || mod10 >= 5 || mod100 is >= 11 and <= 14
                        ? "many"
                        : "other",
            _ => count == 1 ? "one" : "other"
        };
    }
}
