using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Core;
using Veyra.Application.DTOs.Repository.Scanning;

namespace Veyra.Desktop.ViewModels.Pages.Search;

internal sealed record GlobalSearchIndexedEntry(RepositoryDto Repository, RepositoryScanEntryDto Entry);
