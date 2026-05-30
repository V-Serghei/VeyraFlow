using System.Collections.Generic;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Scanning;

namespace Veyra.Infrastructure.Native.Scanning;

internal sealed record NativeScanExecutionResult(
    List<RepositoryScanEntryDto> Entries,
    long NativeScanMs,
    long EntryProjectionMs);
