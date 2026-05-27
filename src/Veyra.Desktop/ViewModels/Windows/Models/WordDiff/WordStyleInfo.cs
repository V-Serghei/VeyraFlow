using System;
using System.Globalization;
using System.Linq;

namespace Veyra.Desktop.ViewModels.Windows;

internal sealed record WordStyleInfo(
    string Raw,
    bool IsParagraph,
    string StyleName,
    string ParagraphStyleName,
    string FontFamily,
    double FontSizePt,
    bool Bold,
    bool Italic,
    bool Underline,
    bool Strike,
    string Foreground,
    string HighlightBackground,
    string HighlightRaw,
    string Alignment,
    string ListDescriptor,
    double SpacingBeforePt,
    double SpacingAfterPt,
    string VerticalAlign,
    string Revision)
{
    public bool IsDefault
        => string.Equals(StyleName, "default", StringComparison.OrdinalIgnoreCase)
           && string.IsNullOrWhiteSpace(ParagraphStyleName)
           && !Bold
           && !Italic
           && !Underline
           && !Strike
           && string.Equals(FontFamily, "Calibri", StringComparison.OrdinalIgnoreCase)
           && Math.Abs(FontSizePt - 14d) < 0.01
           && string.Equals(Foreground, "#111827", StringComparison.OrdinalIgnoreCase)
           && string.IsNullOrWhiteSpace(HighlightRaw)
           && string.IsNullOrWhiteSpace(Alignment)
           && string.IsNullOrWhiteSpace(ListDescriptor)
           && Math.Abs(SpacingBeforePt) < 0.01
           && Math.Abs(SpacingAfterPt) < 0.01
           && string.IsNullOrWhiteSpace(VerticalAlign)
           && string.IsNullOrWhiteSpace(Revision);

    public bool IsEquivalentTo(WordStyleInfo other)
        => Bold == other.Bold
           && Italic == other.Italic
           && Underline == other.Underline
           && Strike == other.Strike
           && string.Equals(FontFamily, other.FontFamily, StringComparison.OrdinalIgnoreCase)
           && Math.Abs(FontSizePt - other.FontSizePt) < 0.01
           && string.Equals(Foreground, other.Foreground, StringComparison.OrdinalIgnoreCase)
           && string.Equals(HighlightRaw, other.HighlightRaw, StringComparison.OrdinalIgnoreCase)
           && string.Equals(StyleName, other.StyleName, StringComparison.OrdinalIgnoreCase)
           && string.Equals(ParagraphStyleName, other.ParagraphStyleName, StringComparison.OrdinalIgnoreCase)
           && string.Equals(Alignment, other.Alignment, StringComparison.OrdinalIgnoreCase)
           && string.Equals(ListDescriptor, other.ListDescriptor, StringComparison.OrdinalIgnoreCase)
           && Math.Abs(SpacingBeforePt - other.SpacingBeforePt) < 0.01
           && Math.Abs(SpacingAfterPt - other.SpacingAfterPt) < 0.01
           && string.Equals(VerticalAlign, other.VerticalAlign, StringComparison.OrdinalIgnoreCase);

    public static WordStyleInfo Default { get; } = new(
        Raw: "default",
        IsParagraph: false,
        StyleName: "default",
        ParagraphStyleName: string.Empty,
        FontFamily: "Calibri",
        FontSizePt: 14d,
        Bold: false,
        Italic: false,
        Underline: false,
        Strike: false,
        Foreground: "#111827",
        HighlightBackground: "Transparent",
        HighlightRaw: string.Empty,
        Alignment: string.Empty,
        ListDescriptor: string.Empty,
        SpacingBeforePt: 0d,
        SpacingAfterPt: 0d,
        VerticalAlign: string.Empty,
        Revision: string.Empty);

    public static WordStyleInfo Parse(string raw, bool isParagraph)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw.Trim(), "default", StringComparison.OrdinalIgnoreCase))
            return Default with { IsParagraph = isParagraph, Raw = raw ?? "default" };

        var styleName = "default";
        var paragraphStyleName = string.Empty;
        var fontFamily = "Calibri";
        var fontSizePt = 14d;
        var bold = false;
        var italic = false;
        var underline = false;
        var strike = false;
        var foreground = "#111827";
        var highlightBackground = "Transparent";
        var highlightRaw = string.Empty;
        var alignment = string.Empty;
        var list = string.Empty;
        var spacingBeforePt = 0d;
        var spacingAfterPt = 0d;
        var verticalAlign = string.Empty;
        var revision = string.Empty;

        foreach (var token in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var lower = token.ToLowerInvariant();
            switch (lower)
            {
                case "b":
                    bold = true;
                    continue;
                case "i":
                    italic = true;
                    continue;
                case "u":
                    underline = true;
                    continue;
                case "strike":
                    strike = true;
                    continue;
            }

            var parts = token.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
                continue;

            var key = parts[0].ToLowerInvariant();
            var value = parts[1];

            switch (key)
            {
                case "style":
                    styleName = string.IsNullOrWhiteSpace(value) ? "default" : value;
                    break;
                case "pstyle":
                    paragraphStyleName = value;
                    break;
                case "font":
                    if (!string.IsNullOrWhiteSpace(value))
                        fontFamily = value;
                    break;
                case "sz":
                    if (value.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
                        value = value[..^2];
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSize))
                        fontSizePt = Math.Clamp(parsedSize, 7d, 40d);
                    break;
                case "u":
                    underline = !string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase);
                    break;
                case "color":
                    foreground = NormalizeColor(value, "#111827");
                    break;
                case "hl":
                    highlightRaw = value;
                    highlightBackground = NormalizeHighlight(value);
                    break;
                case "align":
                    alignment = value;
                    break;
                case "list":
                    list = value;
                    break;
                case "spacing":
                    ParseSpacing(value, out spacingBeforePt, out spacingAfterPt);
                    break;
                case "v":
                    verticalAlign = value;
                    break;
                case "rev":
                    revision = value;
                    break;
            }
        }

        return new WordStyleInfo(
            Raw: raw,
            IsParagraph: isParagraph,
            StyleName: styleName,
            ParagraphStyleName: paragraphStyleName,
            FontFamily: fontFamily,
            FontSizePt: fontSizePt,
            Bold: bold,
            Italic: italic,
            Underline: underline,
            Strike: strike,
            Foreground: foreground,
            HighlightBackground: highlightBackground,
            HighlightRaw: highlightRaw,
            Alignment: alignment,
            ListDescriptor: list,
            SpacingBeforePt: spacingBeforePt,
            SpacingAfterPt: spacingAfterPt,
            VerticalAlign: verticalAlign,
            Revision: revision);
    }

    private static void ParseSpacing(string value, out double beforePt, out double afterPt)
    {
        beforePt = 0d;
        afterPt = 0d;

        if (string.IsNullOrWhiteSpace(value))
            return;

        var parts = value.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length > 0 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var beforeTwips) && beforeTwips >= 0)
            beforePt = beforeTwips / 20d;

        if (parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var afterTwips) && afterTwips >= 0)
            afterPt = afterTwips / 20d;
    }

    private static string NormalizeColor(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var clean = value.Trim().TrimStart('#');
        if (clean.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return fallback;

        if (clean.Length == 6 && clean.All(Uri.IsHexDigit))
            return "#" + clean;

        if (clean.Length == 8 && clean.All(Uri.IsHexDigit))
            return "#" + clean;

        return fallback;
    }

    private static string NormalizeHighlight(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Transparent";

        return value.Trim().ToLowerInvariant() switch
        {
            "yellow" => "#5CFFEB6B",
            "green" => "#4B8FEA9E",
            "cyan" => "#4B9FE7F6",
            "magenta" => "#4BE9B6FF",
            "blue" => "#4B8BB6FF",
            "red" => "#4BFF9DAA",
            "darkyellow" => "#4BC7A23A",
            "darkgreen" => "#4B3F8A66",
            "darkblue" => "#4B4A78B7",
            "gray" => "#4BAAB4C4",
            "lightgray" => "#4BD4DCE8",
            _ => "#4B6AA5D9"
        };
    }
}
