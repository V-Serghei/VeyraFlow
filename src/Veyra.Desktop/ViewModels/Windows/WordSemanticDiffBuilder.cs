using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Veyra.Application.DTOs;

namespace Veyra.Desktop.ViewModels.Windows;

public static class WordSemanticDiffBuilder
{
    private static readonly Regex ParagraphRegex = new(
        @"^P(?<p>\d{4})\s+\[p:(?<style>[^\]]*)\]$",
        RegexOptions.Compiled);

    private static readonly Regex RunRegex = new(
        @"^P(?<p>\d{4})\.R(?<r>\d{4})\s+\[r:(?<style>[^\]]*)\](?:\s(?<text>.*))?$",
        RegexOptions.Compiled);

    public static bool LooksLikeSemanticWordDiff(IReadOnlyList<TextDiffLineDto> lines)
    {
        if (lines.Count == 0)
            return false;

        var semanticHits = 0;
        var sample = Math.Min(lines.Count, 120);

        for (var i = 0; i < sample; i++)
        {
            var text = lines[i].Text?.Trim() ?? string.Empty;
            if (ParagraphRegex.IsMatch(text) || RunRegex.IsMatch(text))
                semanticHits++;
        }

        return semanticHits >= Math.Max(2, sample / 6);
    }

    public static IReadOnlyList<WordSemanticDiffRowViewModel> Build(
        IReadOnlyList<TextDiffLineDto> lines,
        IReadOnlyList<TextDiffHunkDto> hunks)
    {
        if (lines.Count == 0)
            return [];

        var rows = new List<WordSemanticDiffRowViewModel>(lines.Count + (hunks.Count * 2));

        if (hunks.Count > 0)
        {
            foreach (var hunk in hunks.OrderBy(h => h.Sequence))
            {
                var start = Math.Clamp(hunk.StartLineSequence, 0, lines.Count - 1);
                var end = Math.Clamp(hunk.EndLineSequence, start, lines.Count - 1);

                rows.Add(WordSemanticDiffRowViewModel.CreateHunkHeader(
                    FormatHunkRange(hunk.OldStartLine, hunk.OldLineCount),
                    FormatHunkRange(hunk.NewStartLine, hunk.NewLineCount),
                    NormalizeHunkKind(hunk.ChangeKind)));

                AppendRows(lines, start, end, rows);
            }
        }
        else
        {
            rows.Add(WordSemanticDiffRowViewModel.CreateHunkHeader("(full)", "(full)", "context"));
            AppendRows(lines, 0, lines.Count - 1, rows);
        }

        return rows;
    }

    private static void AppendRows(
        IReadOnlyList<TextDiffLineDto> lines,
        int startInclusive,
        int endInclusive,
        ICollection<WordSemanticDiffRowViewModel> rows)
    {
        if (startInclusive > endInclusive)
            return;

        var index = startInclusive;
        while (index <= endInclusive)
        {
            var kind = NormalizeDiffKind(lines[index].Kind);

            if (kind == "remove")
            {
                var removed = new List<TextDiffLineDto>();
                while (index <= endInclusive && NormalizeDiffKind(lines[index].Kind) == "remove")
                {
                    removed.Add(lines[index]);
                    index++;
                }

                var added = new List<TextDiffLineDto>();
                var addCursor = index;
                while (addCursor <= endInclusive && NormalizeDiffKind(lines[addCursor].Kind) == "add")
                {
                    added.Add(lines[addCursor]);
                    addCursor++;
                }

                if (added.Count > 0)
                    index = addCursor;

                var pairCount = Math.Max(removed.Count, added.Count);
                for (var i = 0; i < pairCount; i++)
                {
                    var left = i < removed.Count ? removed[i] : null;
                    var right = i < added.Count ? added[i] : null;
                    var row = CreateRow(left, right);
                    if (row is not null)
                        rows.Add(row);
                }

                continue;
            }

            if (kind == "add")
            {
                while (index <= endInclusive && NormalizeDiffKind(lines[index].Kind) == "add")
                {
                    var row = CreateRow(null, lines[index]);
                    if (row is not null)
                        rows.Add(row);
                    index++;
                }

                continue;
            }

            var equalRow = CreateRow(lines[index], lines[index]);
            if (equalRow is not null)
                rows.Add(equalRow);
            index++;
        }
    }

    private static WordSemanticDiffRowViewModel? CreateRow(TextDiffLineDto? left, TextDiffLineDto? right)
    {
        var leftKind = NormalizeDiffKind(left?.Kind);
        var rightKind = NormalizeDiffKind(right?.Kind);

        var leftParsed = ParseSemanticLine(left?.Text);
        var rightParsed = ParseSemanticLine(right?.Text);

        var bothParagraph = leftParsed.Kind == WordSemanticLineKind.Paragraph && rightParsed.Kind == WordSemanticLineKind.Paragraph;
        if (bothParagraph && string.Equals(leftParsed.Style.Raw, rightParsed.Style.Raw, StringComparison.Ordinal))
            return null;

        var kindBadge = (leftKind, rightKind) switch
        {
            ("remove", "add") => "~",
            ("remove", _) => "-",
            (_, "add") => "+",
            _ => "="
        };

        var isStyleOnlyChange = leftParsed.Kind == WordSemanticLineKind.Run
                                && rightParsed.Kind == WordSemanticLineKind.Run
                                && string.Equals(leftParsed.Text, rightParsed.Text, StringComparison.Ordinal)
                                && !leftParsed.Style.IsEquivalentTo(rightParsed.Style);

        var leftDisplay = BuildDisplayText(leftParsed, leftKind);
        var rightDisplay = BuildDisplayText(rightParsed, rightKind);

        var leftBackground = PickSideBackground(leftKind, isStyleOnlyChange, leftDisplay.Length > 0);
        var rightBackground = PickSideBackground(rightKind, isStyleOnlyChange, rightDisplay.Length > 0);

        return new WordSemanticDiffRowViewModel
        {
            KindBadge = isStyleOnlyChange ? "S" : kindBadge,

            LeftLineNumber = left is null ? string.Empty : FormatLineNumber(left.LeftLineNumber),
            LeftMarker = leftKind switch
            {
                "remove" => "-",
                "equal" => "|",
                _ => " "
            },
            LeftText = leftDisplay,
            LeftFontFamily = leftParsed.Style.FontFamily,
            LeftFontSize = leftParsed.Style.FontSizePt,
            LeftFontWeight = leftParsed.Style.Bold ? FontWeight.Bold : FontWeight.Normal,
            LeftFontStyle = leftParsed.Style.Italic ? FontStyle.Italic : FontStyle.Normal,
            LeftForeground = leftParsed.Style.Foreground,
            LeftTextBackground = leftParsed.Style.HighlightBackground,
            LeftBackground = leftBackground,
            LeftMarkerForeground = leftKind == "remove" ? "#FF9EB1" : "#9BB5D1",
            LeftStyleTag = BuildStyleTag(leftParsed.Style),
            LeftStyleTooltip = BuildStyleTooltip(leftParsed, leftKind),

            RightLineNumber = right is null ? string.Empty : FormatLineNumber(right.RightLineNumber),
            RightMarker = rightKind switch
            {
                "add" => "+",
                "equal" => "|",
                _ => " "
            },
            RightText = rightDisplay,
            RightFontFamily = rightParsed.Style.FontFamily,
            RightFontSize = rightParsed.Style.FontSizePt,
            RightFontWeight = rightParsed.Style.Bold ? FontWeight.Bold : FontWeight.Normal,
            RightFontStyle = rightParsed.Style.Italic ? FontStyle.Italic : FontStyle.Normal,
            RightForeground = rightParsed.Style.Foreground,
            RightTextBackground = rightParsed.Style.HighlightBackground,
            RightBackground = rightBackground,
            RightMarkerForeground = rightKind == "add" ? "#8CFFD0" : "#9BB5D1",
            RightStyleTag = BuildStyleTag(rightParsed.Style),
            RightStyleTooltip = BuildStyleTooltip(rightParsed, rightKind)
        };
    }

    private static string BuildDisplayText(ParsedSemanticLine line, string kind)
    {
        if (line.Kind == WordSemanticLineKind.Paragraph)
        {
            var paragraphStyle = line.Style.StyleName;
            if (!string.IsNullOrWhiteSpace(line.Style.Alignment) || !string.IsNullOrWhiteSpace(line.Style.ListDescriptor))
            {
                var extra = string.Join(", ",
                    new[]
                    {
                        string.IsNullOrWhiteSpace(line.Style.Alignment) ? null : $"align {line.Style.Alignment}",
                        string.IsNullOrWhiteSpace(line.Style.ListDescriptor) ? null : $"list {line.Style.ListDescriptor}"
                    }.Where(x => x is not null)!);

                return $"Paragraph style: {paragraphStyle} ({extra})";
            }

            return $"Paragraph style: {paragraphStyle}";
        }

        if (line.Kind == WordSemanticLineKind.Run)
        {
            if (string.IsNullOrWhiteSpace(line.Text))
                return "<empty run>";

            return line.Text;
        }

        if (string.IsNullOrWhiteSpace(line.Raw))
            return kind == "add" ? "<added>" : kind == "remove" ? "<removed>" : string.Empty;

        return line.Raw;
    }

    private static string BuildStyleTag(WordStyleInfo style)
    {
        if (style.IsDefault)
            return string.Empty;

        var parts = new List<string>(6);
        if (!string.IsNullOrWhiteSpace(style.StyleName) && !string.Equals(style.StyleName, "default", StringComparison.OrdinalIgnoreCase))
            parts.Add(style.StyleName);
        if (style.Bold) parts.Add("B");
        if (style.Italic) parts.Add("I");
        if (style.Underline) parts.Add("U");
        if (style.Strike) parts.Add("S");
        if (!string.IsNullOrWhiteSpace(style.FontFamily) && !string.Equals(style.FontFamily, "Segoe UI Variable", StringComparison.OrdinalIgnoreCase))
            parts.Add(style.FontFamily);
        if (Math.Abs(style.FontSizePt - 14d) > 0.01)
            parts.Add($"{style.FontSizePt:0.#}pt");

        return string.Join(" ", parts.Take(4));
    }

    private static string BuildStyleTooltip(ParsedSemanticLine line, string kind)
    {
        if (line.Kind == WordSemanticLineKind.None)
            return string.Empty;

        var parts = new List<string>
        {
            kind switch
            {
                "add" => "Change: Added",
                "remove" => "Change: Removed",
                _ => "Change: Context"
            },
            line.Kind == WordSemanticLineKind.Paragraph ? "Type: Paragraph" : "Type: Text run",
            $"Style: {line.Style.StyleName}",
            $"Font: {line.Style.FontFamily}",
            $"Size: {line.Style.FontSizePt:0.#}pt",
            $"Bold: {(line.Style.Bold ? "Yes" : "No")}",
            $"Italic: {(line.Style.Italic ? "Yes" : "No")}",
            $"Underline: {(line.Style.Underline ? "Yes" : "No")}",
            $"Strike: {(line.Style.Strike ? "Yes" : "No")}"
        };

        if (!string.IsNullOrWhiteSpace(line.Style.Foreground) && !string.Equals(line.Style.Foreground, "#F5FAFF", StringComparison.OrdinalIgnoreCase))
            parts.Add($"Text color: {line.Style.Foreground}");

        if (!string.IsNullOrWhiteSpace(line.Style.HighlightRaw))
            parts.Add($"Highlight: {line.Style.HighlightRaw}");

        if (!string.IsNullOrWhiteSpace(line.Style.Alignment))
            parts.Add($"Alignment: {line.Style.Alignment}");

        if (!string.IsNullOrWhiteSpace(line.Style.ListDescriptor))
            parts.Add($"List: {line.Style.ListDescriptor}");

        return string.Join(Environment.NewLine, parts);
    }

    private static string PickSideBackground(string sideKind, bool isStyleOnlyChange, bool hasText)
    {
        if (!hasText)
            return "#10233A";

        if (isStyleOnlyChange)
            return "#2A3956";

        return sideKind switch
        {
            "remove" => "#4A2330",
            "add" => "#204838",
            _ => "#173149"
        };
    }

    private static ParsedSemanticLine ParseSemanticLine(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return ParsedSemanticLine.None;

        var text = raw.Trim();

        var paragraph = ParagraphRegex.Match(text);
        if (paragraph.Success)
        {
            var style = WordStyleInfo.Parse(paragraph.Groups["style"].Value, isParagraph: true);
            return new ParsedSemanticLine(WordSemanticLineKind.Paragraph, style, string.Empty, text);
        }

        var run = RunRegex.Match(text);
        if (run.Success)
        {
            var style = WordStyleInfo.Parse(run.Groups["style"].Value, isParagraph: false);
            var runText = run.Groups["text"].Success ? run.Groups["text"].Value : string.Empty;
            return new ParsedSemanticLine(WordSemanticLineKind.Run, style, runText, text);
        }

        return new ParsedSemanticLine(WordSemanticLineKind.None, WordStyleInfo.Default, text, text);
    }

    private static string NormalizeDiffKind(string? kind)
    {
        if (string.Equals(kind, "add", StringComparison.OrdinalIgnoreCase))
            return "add";
        if (string.Equals(kind, "remove", StringComparison.OrdinalIgnoreCase))
            return "remove";
        return "equal";
    }

    private static string NormalizeHunkKind(string? kind)
    {
        if (string.Equals(kind, "added", StringComparison.OrdinalIgnoreCase))
            return "added";
        if (string.Equals(kind, "removed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "deleted", StringComparison.OrdinalIgnoreCase))
            return "removed";
        return "modified";
    }

    private static string FormatLineNumber(int? lineNumber)
        => lineNumber is int value ? value.ToString("D4") : string.Empty;

    private static string FormatHunkRange(int startLine, int count)
        => count <= 0 ? $"{Math.Max(0, startLine)}" : $"{Math.Max(0, startLine)},{count}";

    private enum WordSemanticLineKind
    {
        None = 0,
        Paragraph = 1,
        Run = 2
    }

    private sealed record ParsedSemanticLine(
        WordSemanticLineKind Kind,
        WordStyleInfo Style,
        string Text,
        string Raw)
    {
        public static ParsedSemanticLine None { get; } = new(WordSemanticLineKind.None, WordStyleInfo.Default, string.Empty, string.Empty);
    }

    private sealed record WordStyleInfo(
        string Raw,
        bool IsParagraph,
        string StyleName,
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
        string Revision)
    {
        public bool IsDefault
            => string.Equals(StyleName, "default", StringComparison.OrdinalIgnoreCase)
               && !Bold
               && !Italic
               && !Underline
               && !Strike
               && string.Equals(FontFamily, "Segoe UI Variable", StringComparison.OrdinalIgnoreCase)
               && Math.Abs(FontSizePt - 14d) < 0.01
               && string.Equals(Foreground, "#F5FAFF", StringComparison.OrdinalIgnoreCase)
               && string.IsNullOrWhiteSpace(HighlightRaw)
               && string.IsNullOrWhiteSpace(Alignment)
               && string.IsNullOrWhiteSpace(ListDescriptor)
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
               && string.Equals(Alignment, other.Alignment, StringComparison.OrdinalIgnoreCase)
               && string.Equals(ListDescriptor, other.ListDescriptor, StringComparison.OrdinalIgnoreCase);

        public static WordStyleInfo Default { get; } = new(
            Raw: "default",
            IsParagraph: false,
            StyleName: "default",
            FontFamily: "Segoe UI Variable",
            FontSizePt: 14d,
            Bold: false,
            Italic: false,
            Underline: false,
            Strike: false,
            Foreground: "#F5FAFF",
            HighlightBackground: "Transparent",
            HighlightRaw: string.Empty,
            Alignment: string.Empty,
            ListDescriptor: string.Empty,
            Revision: string.Empty);

        public static WordStyleInfo Parse(string raw, bool isParagraph)
        {
            if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw.Trim(), "default", StringComparison.OrdinalIgnoreCase))
                return Default with { IsParagraph = isParagraph, Raw = raw ?? "default" };

            var styleName = "default";
            var fontFamily = "Segoe UI Variable";
            var fontSizePt = 14d;
            var bold = false;
            var italic = false;
            var underline = false;
            var strike = false;
            var foreground = "#F5FAFF";
            var highlightBackground = "Transparent";
            var highlightRaw = string.Empty;
            var alignment = string.Empty;
            var list = string.Empty;
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
                    case "font":
                        if (!string.IsNullOrWhiteSpace(value))
                            fontFamily = value;
                        break;
                    case "sz":
                        if (value.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
                            value = value[..^2];
                        if (double.TryParse(value, out var parsedSize))
                            fontSizePt = Math.Clamp(parsedSize, 7d, 40d);
                        break;
                    case "color":
                        foreground = NormalizeColor(value, "#F5FAFF");
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
                    case "rev":
                        revision = value;
                        break;
                }
            }

            return new WordStyleInfo(
                Raw: raw,
                IsParagraph: isParagraph,
                StyleName: styleName,
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
                Revision: revision);
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
}
