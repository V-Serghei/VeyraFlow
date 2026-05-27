namespace Veyra.Desktop.ViewModels.Pages.Search;

public sealed record GlobalSearchRepositoryResultItemViewModel(
    int RepositoryId,
    string Name,
    string DirectoryPath,
    string Description,
    string FormatsText,
    string MetricsText,
    string StatusText,
    string LastScannedText,
    bool HasDescription,
    bool HasFormats);
