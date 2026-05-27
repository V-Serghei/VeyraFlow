using System;
using System.Collections.Generic;

namespace Veyra.Infrastructure.Native.Interop;

internal sealed record NativeLibraryProbe(
    IntPtr Handle,
    string? LoadedPath,
    HashSet<string> ExportedEntrypoints,
    IReadOnlyList<string> CandidatePaths,
    string? LoadError);
