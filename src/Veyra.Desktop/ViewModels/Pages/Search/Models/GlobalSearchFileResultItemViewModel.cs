namespace Veyra.Desktop.ViewModels.Pages.Search;

public sealed record GlobalSearchFileResultItemViewModel(
    int RepositoryId,
    string RepositoryName,
    string RelativePath,
    bool IsDirectory,
    string Name,
    string ParentPath,
    string KindText,
    string ExtensionText,
    string SizeText,
    string ModifiedText,
    string RepositoryBadgeText,
    bool HasExtension,
    bool HasParentPath);
