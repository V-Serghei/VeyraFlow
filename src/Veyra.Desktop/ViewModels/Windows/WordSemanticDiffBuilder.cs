using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Layout;
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

    private static readonly Regex TokenRegex = new(
        @"(\r\n|\n|\t|[ ]+|[\p{L}\p{N}_]+|[^\p{L}\p{N}_\s])",
        RegexOptions.Compiled);

    public static bool LooksLikeSemanticWordDiff(IReadOnlyList<TextDiffLineDto> lines)
    {
        if (lines.Count == 0)
            return false;

        var sample = Math.Min(lines.Count, 180);
        var hits = 0;

        for (var i = 0; i < sample; i++)
        {
            var text = lines[i].Text?.TrimStart() ?? string.Empty;
            if (text.Length == 0)
                continue;

            if (ParagraphRegex.IsMatch(text) || RunRegex.IsMatch(text))
            {
                hits += 2;
                continue;
            }

            if (text.StartsWith("P", StringComparison.Ordinal) &&
                (text.Contains("[p:", StringComparison.Ordinal) || text.Contains("[r:", StringComparison.Ordinal)))
            {
                hits++;
            }
        }

        return hits >= Math.Max(6, sample / 7);
    }

    public static IReadOnlyList<WordSemanticDiffRowViewModel> Build(
        IReadOnlyList<TextDiffLineDto> lines,
        IReadOnlyList<TextDiffHunkDto> _)
    {
        if (lines.Count == 0)
            return [];

        var cleaned = lines
            .Where(l => !IsNoiseSemanticLine(l.Text))
            .ToList();

        if (cleaned.Count == 0)
            return [];

        var rows = new List<WordSemanticDiffRowViewModel>(cleaned.Count);
        AppendRows(cleaned, 0, cleaned.Count - 1, rows);
        return rows;
    }

    private static bool IsNoiseSemanticLine(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        var t = raw.Trim();
        if (t.Contains("<object:field>", StringComparison.OrdinalIgnoreCase)
            || t.Contains("<object:drawing>", StringComparison.OrdinalIgnoreCase)
            || t.Contains("<object:ole>", StringComparison.OrdinalIgnoreCase))
            return true;

        // Paragraph descriptor-only rows are not rendered as visual lines.
        if (ParagraphRegex.IsMatch(t))
            return true;

        return false;
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

        if (leftParsed.Kind == WordSemanticLineKind.Paragraph && rightParsed.Kind == WordSemanticLineKind.Paragraph)
            return null;

        var leftDisplay = BuildDisplayText(leftParsed);
        var rightDisplay = BuildDisplayText(rightParsed);

        if (string.IsNullOrWhiteSpace(leftDisplay) && string.IsNullOrWhiteSpace(rightDisplay))
            return null;

        var isStyleOnlyChange = leftParsed.Kind == WordSemanticLineKind.Run
                                && rightParsed.Kind == WordSemanticLineKind.Run
                                && string.Equals(leftDisplay, rightDisplay, StringComparison.Ordinal)
                                && !leftParsed.Style.IsEquivalentTo(rightParsed.Style);

        var isTextChanged = !string.Equals(leftDisplay, rightDisplay, StringComparison.Ordinal);
        var hasStructuralChange = leftKind != "equal" || rightKind != "equal";
        var hasChangeDetails = hasStructuralChange || isStyleOnlyChange || isTextChanged;

        var kindBadge = isStyleOnlyChange
            ? "S"
            : (leftKind, rightKind) switch
            {
                ("remove", "add") => "~",
                ("remove", _) => "-",
                (_, "add") => "+",
                _ => "="
            };

        var leftTooltip = hasChangeDetails
            ? BuildStyleTooltip(leftParsed, rightParsed, leftKind, isStyleOnlyChange, isTextChanged, isLeftSide: true)
            : string.Empty;

        var rightTooltip = hasChangeDetails
            ? BuildStyleTooltip(rightParsed, leftParsed, rightKind, isStyleOnlyChange, isTextChanged, isLeftSide: false)
            : string.Empty;

        var (leftTokens, rightTokens) = BuildTokenLayouts(
            leftDisplay,
            rightDisplay,
            leftParsed.Style,
            rightParsed.Style,
            leftTooltip,
            rightTooltip,
            isStyleOnlyChange,
            isTextChanged);

        var leftAlignment = ParseHorizontalAlignment(leftParsed.Style.Alignment);
        var rightAlignment = ParseHorizontalAlignment(rightParsed.Style.Alignment);

        return new WordSemanticDiffRowViewModel
        {
            IsChanged = hasChangeDetails,
            KindBadge = kindBadge,

            LeftLineNumber = string.Empty,
            LeftMarker = string.Empty,
            LeftText = leftDisplay,
            LeftFontFamily = leftParsed.Style.FontFamily,
            LeftFontSize = BuildRenderedFontSize(leftParsed.Style),
            LeftFontWeight = leftParsed.Style.Bold ? FontWeight.Bold : FontWeight.Normal,
            LeftFontStyle = leftParsed.Style.Italic ? FontStyle.Italic : FontStyle.Normal,
            LeftForeground = leftParsed.Style.Foreground,
            LeftTextBackground = leftParsed.Style.HighlightBackground,
            LeftBackground = "Transparent",
            LeftBorderBrush = PickSideBorder(leftKind, isStyleOnlyChange, hasChangeDetails),
            LeftBorderThickness = hasChangeDetails ? new Thickness(1) : new Thickness(0),
            LeftMarkerForeground = "Transparent",
            LeftStyleTag = BuildStyleTag(leftParsed.Style),
            LeftStyleTooltip = leftTooltip,
            LeftParagraphMargin = BuildParagraphMargin(leftParsed.Style),
            LeftParagraphAlignment = leftAlignment,
            LeftTextAlignment = MapTextAlignment(leftAlignment),
            LeftTextDecorations = BuildTextDecorations(leftParsed.Style),
            LeftTextMargin = BuildTextMargin(leftParsed.Style),
            LeftTokens = leftTokens,

            RightLineNumber = string.Empty,
            RightMarker = string.Empty,
            RightText = rightDisplay,
            RightFontFamily = rightParsed.Style.FontFamily,
            RightFontSize = BuildRenderedFontSize(rightParsed.Style),
            RightFontWeight = rightParsed.Style.Bold ? FontWeight.Bold : FontWeight.Normal,
            RightFontStyle = rightParsed.Style.Italic ? FontStyle.Italic : FontStyle.Normal,
            RightForeground = rightParsed.Style.Foreground,
            RightTextBackground = rightParsed.Style.HighlightBackground,
            RightBackground = "Transparent",
            RightBorderBrush = PickSideBorder(rightKind, isStyleOnlyChange, hasChangeDetails),
            RightBorderThickness = hasChangeDetails ? new Thickness(1) : new Thickness(0),
            RightMarkerForeground = "Transparent",
            RightStyleTag = BuildStyleTag(rightParsed.Style),
            RightStyleTooltip = rightTooltip,
            RightParagraphMargin = BuildParagraphMargin(rightParsed.Style),
            RightParagraphAlignment = rightAlignment,
            RightTextAlignment = MapTextAlignment(rightAlignment),
            RightTextDecorations = BuildTextDecorations(rightParsed.Style),
            RightTextMargin = BuildTextMargin(rightParsed.Style),
            RightTokens = rightTokens
        };
    }
    private static (IReadOnlyList<WordSemanticTokenViewModel> Left, IReadOnlyList<WordSemanticTokenViewModel> Right) BuildTokenLayouts(
        string leftText,
        string rightText,
        WordStyleInfo leftStyle,
        WordStyleInfo rightStyle,
        string leftTooltip,
        string rightTooltip,
        bool isStyleOnlyChange,
        bool isTextChanged)
    {
        if ((!isStyleOnlyChange && !isTextChanged) || (leftText.Length == 0 && rightText.Length == 0))
            return ([], []);

        if (leftText.Length > 8000 || rightText.Length > 8000 || leftText.Contains('\n') || rightText.Contains('\n'))
            return ([], []);

        if (isStyleOnlyChange)
        {
            var left = BuildStyleOnlyTokens(leftText, leftStyle, leftTooltip);
            var right = BuildStyleOnlyTokens(rightText, rightStyle, rightTooltip);
            return (left, right);
        }

        var leftTokens = TokenizePreservingWhitespace(leftText);
        var rightTokens = TokenizePreservingWhitespace(rightText);
        var ops = BuildTokenOperations(leftTokens, rightTokens);

        var equalOps = ops.Count(o => o.Kind == TokenOpKind.Equal && !string.IsNullOrWhiteSpace(o.Value));
        var pivot = Math.Max(1, Math.Min(leftTokens.Count, rightTokens.Count));
        var equalRatio = (double)equalOps / pivot;

        // If line-level semantic projection is noisy (many split runs from DOCX internals),
        // fallback to plain paragraph rendering instead of token boxes.
        if (equalRatio < 0.55d && Math.Max(leftTokens.Count, rightTokens.Count) > 10)
            return ([], []);

        var leftResult = new List<WordSemanticTokenViewModel>(leftTokens.Count);
        var rightResult = new List<WordSemanticTokenViewModel>(rightTokens.Count);

        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case TokenOpKind.Equal:
                    leftResult.Add(CreateToken(op.Value, leftStyle, "Transparent", "Transparent", string.Empty));
                    rightResult.Add(CreateToken(op.Value, rightStyle, "Transparent", "Transparent", string.Empty));
                    break;
                case TokenOpKind.Remove:
                    leftResult.Add(CreateToken(op.Value, leftStyle, "#FFF7F8", "#E7B0BB", BuildTokenTooltip("Removed", op.Value, leftTooltip)));
                    break;
                case TokenOpKind.Add:
                    rightResult.Add(CreateToken(op.Value, rightStyle, "#F3FCF7", "#9CCFB3", BuildTokenTooltip("Added", op.Value, rightTooltip)));
                    break;
            }
        }

        return (leftResult, rightResult);
    }

    private static IReadOnlyList<WordSemanticTokenViewModel> BuildStyleOnlyTokens(string text, WordStyleInfo style, string styleTooltip)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var tokens = TokenizePreservingWhitespace(text);
        if (tokens.Count > 120)
            return [];

        var result = new List<WordSemanticTokenViewModel>(tokens.Count);
        foreach (var token in tokens)
        {
            result.Add(CreateToken(token, style, "#FAF7FF", "#B9A7E8", BuildTokenTooltip("Formatting changed", token, styleTooltip)));
        }

        return result;
    }

    private static WordSemanticTokenViewModel CreateToken(string value, WordStyleInfo style, string background, string outlineBrush, string tooltip)
    {
        var effectiveBackground = string.Equals(background, "Transparent", StringComparison.OrdinalIgnoreCase)
            ? style.HighlightBackground
            : background;

        return new WordSemanticTokenViewModel
        {
            Text = value,
            IsChanged = !string.Equals(outlineBrush, "Transparent", StringComparison.OrdinalIgnoreCase),
            Background = effectiveBackground,
            Tooltip = tooltip,
            FontFamily = style.FontFamily,
            FontSize = BuildRenderedFontSize(style),
            FontWeight = style.Bold ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal,
            Foreground = style.Foreground,
            TextDecorations = BuildTextDecorations(style),
            TextMargin = BuildTextMargin(style),
            OutlineBrush = outlineBrush,
            OutlineThickness = string.Equals(outlineBrush, "Transparent", StringComparison.OrdinalIgnoreCase) ? new Thickness(0) : new Thickness(1)
        };
    }

    private static string BuildTokenTooltip(string changeKind, string token, string styleTooltip)
    {
        var label = DescribeToken(token);
        if (string.IsNullOrWhiteSpace(styleTooltip))
            return $"{changeKind}: {label}";

        return $"{changeKind}: {label}{Environment.NewLine}{styleTooltip}";
    }

    private static string DescribeToken(string token)
    {
        if (string.IsNullOrEmpty(token))
            return "<empty>";

        if (token == " ")
            return "space";

        if (token.All(char.IsWhiteSpace))
            return token.Contains('\t') ? "tab/whitespace" : "whitespace";

        return '"' + token + '"';
    }

    private static IReadOnlyList<string> TokenizePreservingWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var tokens = new List<string>();
        var matches = TokenRegex.Matches(text);
        if (matches.Count == 0)
            return [text];

        foreach (Match match in matches)
        {
            if (match.Success && match.Length > 0)
                tokens.Add(match.Value);
        }

        return tokens.Count == 0 ? [text] : tokens;
    }

    private static IReadOnlyList<TokenOperation> BuildTokenOperations(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var n = left.Count;
        var m = right.Count;
        var dp = new int[n + 1, m + 1];

        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                if (string.Equals(left[i], right[j], StringComparison.Ordinal))
                    dp[i, j] = 1 + dp[i + 1, j + 1];
                else
                    dp[i, j] = Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var result = new List<TokenOperation>(Math.Max(n, m) * 2);
        var li = 0;
        var ri = 0;

        while (li < n && ri < m)
        {
            if (string.Equals(left[li], right[ri], StringComparison.Ordinal))
            {
                result.Add(new TokenOperation(TokenOpKind.Equal, left[li]));
                li++;
                ri++;
                continue;
            }

            if (dp[li + 1, ri] >= dp[li, ri + 1])
            {
                result.Add(new TokenOperation(TokenOpKind.Remove, left[li]));
                li++;
            }
            else
            {
                result.Add(new TokenOperation(TokenOpKind.Add, right[ri]));
                ri++;
            }
        }

        while (li < n)
        {
            result.Add(new TokenOperation(TokenOpKind.Remove, left[li]));
            li++;
        }

        while (ri < m)
        {
            result.Add(new TokenOperation(TokenOpKind.Add, right[ri]));
            ri++;
        }

        return result;
    }

    private static string BuildDisplayText(ParsedSemanticLine line)
    {
        if (line.Kind == WordSemanticLineKind.Paragraph)
            return string.Empty;

        if (line.Kind == WordSemanticLineKind.Run)
        {
            if (string.Equals(line.Text, "<empty-paragraph>", StringComparison.Ordinal))
                return string.Empty;

            var decoded = DecodeText(line.Text);
            if (decoded.Contains("<object:", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            return decoded;
        }

        return DecodeText(line.Raw);
    }

    private static string DecodeText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != '\\' || i == text.Length - 1)
            {
                sb.Append(ch);
                continue;
            }

            var next = text[i + 1];
            switch (next)
            {
                case 'n':
                    sb.Append('\n');
                    i++;
                    break;
                case 't':
                    sb.Append('\t');
                    i++;
                    break;
                case '\\':
                    sb.Append('\\');
                    i++;
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }

        return sb.ToString();
    }

    private static string BuildStyleTag(WordStyleInfo style)
    {
        if (style.IsDefault)
            return string.Empty;

        var parts = new List<string>(7);
        if (!string.IsNullOrWhiteSpace(style.StyleName) && !string.Equals(style.StyleName, "default", StringComparison.OrdinalIgnoreCase))
            parts.Add(style.StyleName);
        if (!string.IsNullOrWhiteSpace(style.ParagraphStyleName) && !string.Equals(style.ParagraphStyleName, "default", StringComparison.OrdinalIgnoreCase))
            parts.Add("P:" + style.ParagraphStyleName);
        if (style.Bold) parts.Add("B");
        if (style.Italic) parts.Add("I");
        if (style.Underline) parts.Add("U");
        if (style.Strike) parts.Add("S");
        if (!string.IsNullOrWhiteSpace(style.FontFamily) && !string.Equals(style.FontFamily, "Calibri", StringComparison.OrdinalIgnoreCase))
            parts.Add(style.FontFamily);
        if (Math.Abs(style.FontSizePt - 14d) > 0.01)
            parts.Add($"{style.FontSizePt:0.#}pt");

        return string.Join(" ", parts.Take(4));
    }

    private static string BuildStyleTooltip(
        ParsedSemanticLine line,
        ParsedSemanticLine other,
        string selfKind,
        bool isStyleOnlyChange,
        bool isTextChanged,
        bool isLeftSide)
    {
        if (line.Kind == WordSemanticLineKind.None)
            return string.Empty;

        var changeLabel = selfKind switch
        {
            "add" => isLeftSide ? "Added on LEFT side" : "Added on RIGHT side",
            "remove" => isLeftSide ? "Removed from LEFT side" : "Removed from RIGHT side",
            _ when isStyleOnlyChange => "Formatting changed",
            _ when isTextChanged => "Text changed",
            _ => "Changed"
        };

        var parts = new List<string>
        {
            "Diff: " + changeLabel,
            line.Kind == WordSemanticLineKind.Paragraph ? "Type: Paragraph" : "Type: Text",
            "Style: " + NormalizeLabel(line.Style.StyleName),
            "Font: " + line.Style.FontFamily,
            $"Size: {line.Style.FontSizePt:0.#}pt",
            "Bold: " + (line.Style.Bold ? "Yes" : "No"),
            "Italic: " + (line.Style.Italic ? "Yes" : "No"),
            "Underline: " + (line.Style.Underline ? "Yes" : "No"),
            "Strike: " + (line.Style.Strike ? "Yes" : "No"),
            "Vertical align: " + NormalizeLabel(line.Style.VerticalAlign)
        };
        if (other.Kind != WordSemanticLineKind.None)
            AddStyleDelta(parts, line.Style, other.Style);

        if (line.Kind != other.Kind && other.Kind != WordSemanticLineKind.None)
            parts.Add($"Structure: {DescribeKind(other.Kind)} -> {DescribeKind(line.Kind)}");

        if (isTextChanged && line.Kind == WordSemanticLineKind.Run && other.Kind == WordSemanticLineKind.Run)
            parts.Add("Run text changed.");

        if (!string.IsNullOrWhiteSpace(line.Style.Foreground) && !string.Equals(line.Style.Foreground, "#111827", StringComparison.OrdinalIgnoreCase))
            parts.Add("Text color: " + line.Style.Foreground);

        if (!string.IsNullOrWhiteSpace(line.Style.HighlightRaw))
            parts.Add("Highlight: " + line.Style.HighlightRaw);

        if (!string.IsNullOrWhiteSpace(line.Style.Alignment))
            parts.Add("Alignment: " + line.Style.Alignment);

        if (!string.IsNullOrWhiteSpace(line.Style.ListDescriptor))
            parts.Add("List: " + line.Style.ListDescriptor);

        return string.Join(Environment.NewLine, parts);
    }

    private static void AddStyleDelta(List<string> parts, WordStyleInfo current, WordStyleInfo previous)
    {
        if (current.IsEquivalentTo(previous))
            return;

        if (!string.Equals(previous.StyleName, current.StyleName, StringComparison.OrdinalIgnoreCase))
            parts.Add($"Style changed: {NormalizeLabel(previous.StyleName)} -> {NormalizeLabel(current.StyleName)}");

        if (!string.Equals(previous.ParagraphStyleName, current.ParagraphStyleName, StringComparison.OrdinalIgnoreCase))
            parts.Add($"Paragraph style changed: {NormalizeLabel(previous.ParagraphStyleName)} -> {NormalizeLabel(current.ParagraphStyleName)}");

        if (!string.Equals(previous.FontFamily, current.FontFamily, StringComparison.OrdinalIgnoreCase))
            parts.Add($"Font changed: {previous.FontFamily} -> {current.FontFamily}");

        if (Math.Abs(previous.FontSizePt - current.FontSizePt) > 0.01)
            parts.Add($"Size changed: {previous.FontSizePt:0.#}pt -> {current.FontSizePt:0.#}pt");

        if (previous.Bold != current.Bold)
            parts.Add($"Bold: {(previous.Bold ? "On" : "Off")} -> {(current.Bold ? "On" : "Off")}");

        if (previous.Italic != current.Italic)
            parts.Add($"Italic: {(previous.Italic ? "On" : "Off")} -> {(current.Italic ? "On" : "Off")}");

        if (previous.Underline != current.Underline)
            parts.Add($"Underline: {(previous.Underline ? "On" : "Off")} -> {(current.Underline ? "On" : "Off")}");

        if (previous.Strike != current.Strike)
            parts.Add($"Strike: {(previous.Strike ? "On" : "Off")} -> {(current.Strike ? "On" : "Off")}");

        if (!string.Equals(previous.VerticalAlign, current.VerticalAlign, StringComparison.OrdinalIgnoreCase))
            parts.Add($"Vertical align changed: {NormalizeLabel(previous.VerticalAlign)} -> {NormalizeLabel(current.VerticalAlign)}");

        if (!string.Equals(previous.Foreground, current.Foreground, StringComparison.OrdinalIgnoreCase))
            parts.Add($"Text color changed: {previous.Foreground} -> {current.Foreground}");

        if (!string.Equals(previous.HighlightRaw, current.HighlightRaw, StringComparison.OrdinalIgnoreCase))
            parts.Add($"Highlight changed: {NormalizeLabel(previous.HighlightRaw)} -> {NormalizeLabel(current.HighlightRaw)}");

        if (!string.Equals(previous.Alignment, current.Alignment, StringComparison.OrdinalIgnoreCase))
            parts.Add($"Alignment changed: {NormalizeLabel(previous.Alignment)} -> {NormalizeLabel(current.Alignment)}");

        if (!string.Equals(previous.ListDescriptor, current.ListDescriptor, StringComparison.OrdinalIgnoreCase))
            parts.Add($"List changed: {NormalizeLabel(previous.ListDescriptor)} -> {NormalizeLabel(current.ListDescriptor)}");
    }

    private static string DescribeKind(WordSemanticLineKind kind)
    {
        return kind switch
        {
            WordSemanticLineKind.Paragraph => "Paragraph",
            WordSemanticLineKind.Run => "Text run",
            _ => "Unknown"
        };
    }

    private static string NormalizeLabel(string? value)
        => string.IsNullOrWhiteSpace(value) ? "(none)" : value;

    private static string PickSideBorder(string sideKind, bool isStyleOnlyChange, bool hasChangeDetails)
    {
        if (!hasChangeDetails)
            return "Transparent";

        if (isStyleOnlyChange)
            return "#B9A7E8";

        return sideKind switch
        {
            "remove" => "#E7B0BB",
            "add" => "#A5DABD",
            _ => "#A7C2DE"
        };
    }

    private static Thickness BuildParagraphMargin(WordStyleInfo style)
    {
        var left = 0d;

        if (!string.IsNullOrWhiteSpace(style.ListDescriptor))
        {
            var level = ParseListLevel(style.ListDescriptor);
            left = 20d + (level * 18d);
        }

        var top = style.SpacingBeforePt > 0 ? Math.Clamp(style.SpacingBeforePt * 0.45d, 0d, 24d) : 0d;
        var bottom = style.SpacingAfterPt > 0 ? Math.Clamp(style.SpacingAfterPt * 0.45d, 2d, 28d) : 4d;

        return new Thickness(left, top, 0d, bottom);
    }

    private static int ParseListLevel(string listDescriptor)
    {
        var parts = listDescriptor.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return 0;

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
            return 0;

        return Math.Clamp(level, 0, 8);
    }

    private static HorizontalAlignment ParseHorizontalAlignment(string alignment)
    {
        return alignment.Trim().ToLowerInvariant() switch
        {
            "center" => HorizontalAlignment.Center,
            "right" => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left
        };
    }

    private static TextAlignment MapTextAlignment(HorizontalAlignment alignment)
    {
        return alignment switch
        {
            HorizontalAlignment.Center => TextAlignment.Center,
            HorizontalAlignment.Right => TextAlignment.Right,
            _ => TextAlignment.Left
        };
    }

    private static double BuildRenderedFontSize(WordStyleInfo style)
    {
        if (IsSuperscript(style.VerticalAlign) || IsSubscript(style.VerticalAlign))
            return Math.Max(8d, style.FontSizePt * 0.78d);

        return style.FontSizePt;
    }

    private static Thickness BuildTextMargin(WordStyleInfo style)
    {
        if (IsSuperscript(style.VerticalAlign))
            return new Thickness(0, -4, 0, 0);

        if (IsSubscript(style.VerticalAlign))
            return new Thickness(0, 3, 0, 0);

        return new Thickness(0);
    }

    private static bool IsSuperscript(string verticalAlign)
        => string.Equals(verticalAlign, "superscript", StringComparison.OrdinalIgnoreCase)
           || string.Equals(verticalAlign, "super", StringComparison.OrdinalIgnoreCase);

    private static bool IsSubscript(string verticalAlign)
        => string.Equals(verticalAlign, "subscript", StringComparison.OrdinalIgnoreCase)
           || string.Equals(verticalAlign, "sub", StringComparison.OrdinalIgnoreCase);

    private static TextDecorationCollection BuildTextDecorations(WordStyleInfo style)
    {
        if (!style.Underline && !style.Strike)
            return [];

        if (style.Underline && style.Strike)
        {
            var both = new TextDecorationCollection();
            foreach (var item in TextDecorations.Underline)
                both.Add(item);
            foreach (var item in TextDecorations.Strikethrough)
                both.Add(item);
            return both;
        }

        if (style.Underline)
            return CloneDecorations(TextDecorations.Underline);

        return CloneDecorations(TextDecorations.Strikethrough);
    }

    private static TextDecorationCollection CloneDecorations(TextDecorationCollection source)
    {
        var clone = new TextDecorationCollection();
        foreach (var item in source)
            clone.Add(item);

        return clone;
    }

    private static ParsedSemanticLine ParseSemanticLine(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return ParsedSemanticLine.None;

        var text = raw.Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (string.IsNullOrWhiteSpace(text))
            return ParsedSemanticLine.None;

        var matchInput = text.TrimStart();
        var paragraph = ParagraphRegex.Match(matchInput);
        if (paragraph.Success)
        {
            var style = WordStyleInfo.Parse(paragraph.Groups["style"].Value, isParagraph: true);
            return new ParsedSemanticLine(WordSemanticLineKind.Paragraph, style, string.Empty, text);
        }

        var run = RunRegex.Match(matchInput);
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

    private enum WordSemanticLineKind
    {
        None = 0,
        Paragraph = 1,
        Run = 2
    }

    private enum TokenOpKind
    {
        Equal = 0,
        Remove = 1,
        Add = 2
    }

    private sealed record TokenOperation(TokenOpKind Kind, string Value);

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
}
