using Veyra.Application.DTOs;

namespace Veyra.Desktop.ViewModels.Pages.Search;

internal sealed record GlobalSearchIndexedEntry(RepositoryDto Repository, RepositoryScanEntryDto Entry);
