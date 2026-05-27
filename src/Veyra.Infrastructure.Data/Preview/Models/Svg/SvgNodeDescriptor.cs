using System.Collections.Generic;

namespace Veyra.Infrastructure.Data.Preview;

internal sealed record SvgNodeDescriptor(
    string Name,
    IReadOnlyDictionary<string, string> Attributes,
    string TextContent);
