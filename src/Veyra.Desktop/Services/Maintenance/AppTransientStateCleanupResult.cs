using System;

namespace Veyra.Desktop.Services.Maintenance;

public sealed record AppTransientStateCleanupResult(
    int DeletedFiles,
    int DeletedDirectories,
    string RootPath);
