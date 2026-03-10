using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Veyra.Application.Services.Diff;

public static class WordSemanticProjection
{
    private static readonly HashSet<string> WordOoxmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".docm", ".dotx", ".dotm"
    };

    public static bool IsWordOoxmlExtension(string? extension)
    {
        var normalized = NormalizeExtension(extension);
        return normalized is not null && WordOoxmlExtensions.Contains(normalized);
    }

    public static IReadOnlyList<string> ExtractSemanticLines(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return ["<word:missing-file>"];

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length == 0)
            return ["<word:empty-file>"];

        using var archive = ZipFile.OpenRead(filePath);

        var styleMap = ReadStyleMap(archive);
        var documentEntry = archive.GetEntry("word/document.xml");
        if (documentEntry is null)
            return ["<word:missing-document-part>"];

        using var stream = documentEntry.Open();
        var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);

        return BuildSemanticLines(document, styleMap);
    }

    private static IReadOnlyList<string> BuildSemanticLines(
        XDocument document,
        IReadOnlyDictionary<string, string> styleMap)
    {
        var w = XNamespace.Get("http://schemas.openxmlformats.org/wordprocessingml/2006/main");
        var body = document.Root?.Element(w + "body");
        if (body is null)
            return ["<word:missing-body>"];

        var paragraphs = body.Descendants(w + "p").ToList();
        if (paragraphs.Count == 0)
            return ["<word:empty-document>"];

        var lines = new List<string>(paragraphs.Count * 2);
        var paragraphIndex = 0;

        foreach (var paragraph in paragraphs)
        {
            paragraphIndex++;
            var paragraphLabel = $"P{paragraphIndex:D4}";
            var paragraphStyle = BuildParagraphStyleToken(paragraph, w, styleMap);
            lines.Add($"{paragraphLabel} [p:{paragraphStyle}]");

            var runIndex = 0;
            var hasContent = false;

            foreach (var run in paragraph.Descendants(w + "r"))
            {
                runIndex++;
                var runStyle = BuildRunStyleToken(run, w, styleMap);
                var effectiveStyle = MergeRunAndParagraphStyle(runStyle, paragraphStyle);
                var runText = ExtractRunText(run, w);

                if (string.IsNullOrEmpty(runText))
                    continue;

                hasContent = true;
                var normalizedText = NormalizeText(runText);
                lines.Add($"{paragraphLabel}.R{runIndex:D4} [r:{effectiveStyle}] {normalizedText}");
            }

            if (!hasContent)
                lines.Add($"{paragraphLabel}.R0000 [r:default] <empty-paragraph>");
        }

        return lines;
    }

    private static string MergeRunAndParagraphStyle(string runStyle, string paragraphStyle)
    {
        var merged = new List<string>(12);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in SplitStyleTokens(runStyle))
        {
            if (string.IsNullOrWhiteSpace(token) || string.Equals(token, "default", StringComparison.OrdinalIgnoreCase))
                continue;

            merged.Add(token);
            var key = GetStyleTokenKey(token);
            if (!string.IsNullOrWhiteSpace(key))
                keys.Add(key);
        }

        foreach (var token in SplitStyleTokens(paragraphStyle))
        {
            if (string.IsNullOrWhiteSpace(token) || string.Equals(token, "default", StringComparison.OrdinalIgnoreCase))
                continue;

            var normalized = token;
            var key = GetStyleTokenKey(token);

            if (string.Equals(key, "style", StringComparison.OrdinalIgnoreCase))
            {
                var idx = token.IndexOf('=');
                var value = idx >= 0 && idx < token.Length - 1 ? token[(idx + 1)..].Trim() : string.Empty;
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                key = "pstyle";
                normalized = "pstyle=" + value;
            }

            if (string.IsNullOrWhiteSpace(key) || keys.Contains(key))
                continue;

            merged.Add(normalized);
            keys.Add(key);
        }

        return merged.Count == 0 ? "default" : string.Join(';', merged);
    }

    private static IEnumerable<string> SplitStyleTokens(string style)
    {
        if (string.IsNullOrWhiteSpace(style))
            yield break;

        foreach (var token in style.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return token;
    }

    private static string GetStyleTokenKey(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return string.Empty;

        var idx = token.IndexOf('=');
        if (idx <= 0)
            return token.Trim().ToLowerInvariant();

        return token[..idx].Trim().ToLowerInvariant();
    }

    private static Dictionary<string, string> ReadStyleMap(ZipArchive archive)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stylesEntry = archive.GetEntry("word/styles.xml");
        if (stylesEntry is null)
            return result;

        using var stream = stylesEntry.Open();
        var styles = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        var w = XNamespace.Get("http://schemas.openxmlformats.org/wordprocessingml/2006/main");

        foreach (var style in styles.Descendants(w + "style"))
        {
            var styleId = style.Attribute(w + "styleId")?.Value;
            if (string.IsNullOrWhiteSpace(styleId))
                continue;

            var styleName = style.Element(w + "name")?.Attribute(w + "val")?.Value;
            result[styleId] = string.IsNullOrWhiteSpace(styleName) ? styleId : styleName;
        }

        return result;
    }

    private static string BuildParagraphStyleToken(
        XElement paragraph,
        XNamespace w,
        IReadOnlyDictionary<string, string> styleMap)
    {
        var pPr = paragraph.Element(w + "pPr");
        if (pPr is null)
            return "default";

        var tokens = new List<string>(8);

        var pStyleId = pPr.Element(w + "pStyle")?.Attribute(w + "val")?.Value;
        if (!string.IsNullOrWhiteSpace(pStyleId))
            tokens.Add("style=" + ResolveStyleName(styleMap, pStyleId));

        var alignment = pPr.Element(w + "jc")?.Attribute(w + "val")?.Value;
        if (!string.IsNullOrWhiteSpace(alignment))
            tokens.Add("align=" + alignment);

        var numPr = pPr.Element(w + "numPr");
        if (numPr is not null)
        {
            var numId = numPr.Element(w + "numId")?.Attribute(w + "val")?.Value;
            var level = numPr.Element(w + "ilvl")?.Attribute(w + "val")?.Value;
            if (!string.IsNullOrWhiteSpace(numId) || !string.IsNullOrWhiteSpace(level))
                tokens.Add($"list={numId ?? "?"}:{level ?? "?"}");
        }

        var spacing = pPr.Element(w + "spacing");
        if (spacing is not null)
        {
            var before = spacing.Attribute(w + "before")?.Value;
            var after = spacing.Attribute(w + "after")?.Value;
            var line = spacing.Attribute(w + "line")?.Value;
            if (!string.IsNullOrWhiteSpace(before) || !string.IsNullOrWhiteSpace(after) || !string.IsNullOrWhiteSpace(line))
                tokens.Add($"spacing={before ?? "-"}/{after ?? "-"}/{line ?? "-"}");
        }

        if (IsOn(pPr.Element(w + "keepNext"), w))
            tokens.Add("keep-next");

        if (IsOn(pPr.Element(w + "pageBreakBefore"), w))
            tokens.Add("page-break-before");

        return tokens.Count == 0 ? "default" : string.Join(';', tokens);
    }

    private static string BuildRunStyleToken(
        XElement run,
        XNamespace w,
        IReadOnlyDictionary<string, string> styleMap)
    {
        var rPr = run.Element(w + "rPr");
        if (rPr is null)
            return "default";

        var tokens = new List<string>(14);

        var runStyleId = rPr.Element(w + "rStyle")?.Attribute(w + "val")?.Value;
        if (!string.IsNullOrWhiteSpace(runStyleId))
            tokens.Add("style=" + ResolveStyleName(styleMap, runStyleId));

        if (IsOn(rPr.Element(w + "b"), w))
            tokens.Add("b");

        if (IsOn(rPr.Element(w + "i"), w))
            tokens.Add("i");

        if (IsOn(rPr.Element(w + "u"), w))
        {
            var underline = rPr.Element(w + "u")?.Attribute(w + "val")?.Value;
            tokens.Add(string.IsNullOrWhiteSpace(underline) || underline == "single" ? "u" : $"u={underline}");
        }

        if (IsOn(rPr.Element(w + "strike"), w))
            tokens.Add("strike");

        if (IsOn(rPr.Element(w + "caps"), w))
            tokens.Add("caps");

        if (IsOn(rPr.Element(w + "smallCaps"), w))
            tokens.Add("smallcaps");

        var color = rPr.Element(w + "color")?.Attribute(w + "val")?.Value;
        if (!string.IsNullOrWhiteSpace(color))
            tokens.Add("color=" + color);

        var highlight = rPr.Element(w + "highlight")?.Attribute(w + "val")?.Value;
        if (!string.IsNullOrWhiteSpace(highlight))
            tokens.Add("hl=" + highlight);

        var sizeHalfPt = rPr.Element(w + "sz")?.Attribute(w + "val")?.Value;
        if (int.TryParse(sizeHalfPt, out var sizeVal) && sizeVal > 0)
            tokens.Add($"sz={sizeVal / 2.0:0.#}pt");

        var fonts = rPr.Element(w + "rFonts");
        var fontName = fonts?.Attribute(w + "ascii")?.Value
                       ?? fonts?.Attribute(w + "hAnsi")?.Value
                       ?? fonts?.Attribute(w + "cs")?.Value;
        if (!string.IsNullOrWhiteSpace(fontName))
            tokens.Add("font=" + fontName);

        var vAlign = rPr.Element(w + "vertAlign")?.Attribute(w + "val")?.Value;
        if (!string.IsNullOrWhiteSpace(vAlign))
            tokens.Add("v=" + vAlign);

        if (run.Ancestors(w + "ins").Any())
            tokens.Add("rev=ins");
        else if (run.Ancestors(w + "del").Any())
            tokens.Add("rev=del");

        return tokens.Count == 0 ? "default" : string.Join(';', tokens);
    }

    private static string ExtractRunText(XElement run, XNamespace w)
    {
        var buffer = new StringBuilder();

        foreach (var node in run.DescendantNodes())
        {
            if (node is not XElement element)
                continue;

            if (element.Name == w + "t")
            {
                buffer.Append(element.Value);
                continue;
            }

            if (element.Name == w + "tab")
            {
                buffer.Append('\t');
                continue;
            }

            if (element.Name == w + "br" || element.Name == w + "cr")
            {
                buffer.Append('\n');
                continue;
            }

            if (element.Name == w + "noBreakHyphen" || element.Name == w + "softHyphen")
                buffer.Append('-');
        }

        return buffer.ToString();
    }

    private static bool IsOn(XElement? element, XNamespace w)
    {
        if (element is null)
            return false;

        var raw = element.Attribute(w + "val")?.Value;
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        if (raw == "0")
            return false;

        if (string.Equals(raw, "none", StringComparison.OrdinalIgnoreCase))
            return false;

        if (bool.TryParse(raw, out var parsed))
            return parsed;

        return !string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveStyleName(IReadOnlyDictionary<string, string> styleMap, string styleId)
        => styleMap.TryGetValue(styleId, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : styleId;

    private static string NormalizeText(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "<empty>";

        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var sb = new StringBuilder(normalized.Length + 16);
        foreach (var ch in normalized)
        {
            switch (ch)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }

        return sb.ToString();
    }

    private static string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return null;

        var normalized = extension.Trim();
        if (!normalized.StartsWith('.'))
            normalized = "." + normalized;

        return normalized.ToLowerInvariant();
    }
}
