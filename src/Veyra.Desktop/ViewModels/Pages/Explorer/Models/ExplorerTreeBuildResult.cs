using System.Collections.Generic;

namespace Veyra.Desktop.ViewModels.Pages.Explorer;

internal sealed record ExplorerTreeBuildResult(
    ExplorerTreeNodeViewModel Root,
    Dictionary<string, ExplorerTreeNodeViewModel> Nodes);
