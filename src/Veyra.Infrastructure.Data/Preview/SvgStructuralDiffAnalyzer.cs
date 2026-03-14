using System.Xml.Linq;

namespace Veyra.Infrastructure.Data.Preview;

public sealed record SvgStructuralDiffSummary(
    int AddedElementCount,
    int RemovedElementCount,
    int ModifiedElementCount,
    int ChangedAttributeCount);

public static class SvgStructuralDiffAnalyzer
{
    public static SvgStructuralDiffSummary? TryAnalyze(string baselinePath, string currentPath)
    {
        if (string.IsNullOrWhiteSpace(baselinePath)
            || string.IsNullOrWhiteSpace(currentPath)
            || !File.Exists(baselinePath)
            || !File.Exists(currentPath)
            || !IsSvgPath(baselinePath)
            || !IsSvgPath(currentPath))
        {
            return null;
        }

        try
        {
            var baselineDocument = XDocument.Load(baselinePath, LoadOptions.None);
            var currentDocument = XDocument.Load(currentPath, LoadOptions.None);
            if (baselineDocument.Root is null || currentDocument.Root is null)
                return null;

            var baselineNodes = BuildNodeMap(baselineDocument.Root);
            var currentNodes = BuildNodeMap(currentDocument.Root);

            var added = 0;
            var removed = 0;
            var modified = 0;
            var changedAttributes = 0;

            foreach (var key in currentNodes.Keys)
            {
                if (!baselineNodes.ContainsKey(key))
                    added++;
            }

            foreach (var key in baselineNodes.Keys)
            {
                if (!currentNodes.ContainsKey(key))
                    removed++;
            }

            foreach (var key in baselineNodes.Keys.Intersect(currentNodes.Keys, StringComparer.Ordinal))
            {
                var baselineNode = baselineNodes[key];
                var currentNode = currentNodes[key];

                var attributeDelta = CountAttributeChanges(baselineNode, currentNode);
                if (attributeDelta <= 0)
                    continue;

                modified++;
                changedAttributes += attributeDelta;
            }

            return new SvgStructuralDiffSummary(added, removed, modified, changedAttributes);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, SvgNodeDescriptor> BuildNodeMap(XElement root)
    {
        var nodes = new Dictionary<string, SvgNodeDescriptor>(StringComparer.Ordinal);
        Walk(root, $"{root.Name.LocalName}[1]", nodes);
        return nodes;
    }

    private static void Walk(XElement element, string path, IDictionary<string, SvgNodeDescriptor> nodes)
    {
        var id = NormalizeValue(element.Attribute("id")?.Value);
        var key = !string.IsNullOrWhiteSpace(id)
            ? $"id:{id}"
            : path;

        var uniqueKey = key;
        var duplicateIndex = 2;
        while (nodes.ContainsKey(uniqueKey))
        {
            uniqueKey = $"{key}#{duplicateIndex}";
            duplicateIndex++;
        }

        nodes[uniqueKey] = new SvgNodeDescriptor(
            element.Name.LocalName,
            BuildAttributeMap(element),
            NormalizeValue(string.Concat(element.Nodes().OfType<XText>().Select(x => x.Value))));

        var childCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in element.Elements())
        {
            var childName = child.Name.LocalName;
            childCounters.TryGetValue(childName, out var index);
            index++;
            childCounters[childName] = index;
            Walk(child, $"{path}/{childName}[{index}]", nodes);
        }
    }

    private static Dictionary<string, string> BuildAttributeMap(XElement element)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
                continue;

            var key = BuildAttributeKey(attribute.Name);
            var value = NormalizeValue(attribute.Value);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            attributes[key] = value;
            if (string.Equals(attribute.Name.LocalName, "style", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var styleEntry in ExpandStyleEntries(value))
                    attributes[styleEntry.Key] = styleEntry.Value;
            }
        }

        return attributes;
    }

    private static int CountAttributeChanges(SvgNodeDescriptor baselineNode, SvgNodeDescriptor currentNode)
    {
        var changed = 0;
        if (!string.Equals(baselineNode.Name, currentNode.Name, StringComparison.Ordinal))
            changed++;

        var baselineKeys = baselineNode.Attributes.Keys.ToHashSet(StringComparer.Ordinal);
        var currentKeys = currentNode.Attributes.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var key in baselineKeys.Union(currentKeys, StringComparer.Ordinal))
        {
            baselineNode.Attributes.TryGetValue(key, out var baselineValue);
            currentNode.Attributes.TryGetValue(key, out var currentValue);
            if (!string.Equals(baselineValue, currentValue, StringComparison.Ordinal))
                changed++;
        }

        if (!string.Equals(baselineNode.TextContent, currentNode.TextContent, StringComparison.Ordinal))
            changed++;

        return changed;
    }

    private static string BuildAttributeKey(XName name)
    {
        if (string.IsNullOrWhiteSpace(name.NamespaceName))
            return name.LocalName;

        return $"{name.NamespaceName}|{name.LocalName}";
    }

    private static IEnumerable<KeyValuePair<string, string>> ExpandStyleEntries(string styleValue)
    {
        foreach (var segment in styleValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = segment.IndexOf(':');
            if (separatorIndex <= 0 || separatorIndex >= segment.Length - 1)
                continue;

            var key = NormalizeValue(segment[..separatorIndex]);
            var value = NormalizeValue(segment[(separatorIndex + 1)..]);
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;

            yield return new KeyValuePair<string, string>($"style.{key}", value);
        }
    }

    private static string NormalizeValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        return string.Join(
            ' ',
            raw.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static bool IsSvgPath(string path)
        => string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase);

    private sealed record SvgNodeDescriptor(
        string Name,
        IReadOnlyDictionary<string, string> Attributes,
        string TextContent);
}
