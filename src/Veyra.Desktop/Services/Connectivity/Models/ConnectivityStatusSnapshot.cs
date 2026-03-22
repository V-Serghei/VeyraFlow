using System;

namespace Veyra.Desktop.Services.Connectivity.Models;

public sealed record ConnectivityStatusSnapshot(
    ConnectivityState State,
    DateTime CheckedAtUtc,
    string? Detail = null);
