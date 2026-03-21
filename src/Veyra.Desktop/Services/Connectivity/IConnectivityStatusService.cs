using System;
using System.Threading;
using System.Threading.Tasks;
using Veyra.Desktop.Services.Connectivity.Models;

namespace Veyra.Desktop.Services.Connectivity;

public interface IConnectivityStatusService
{
    ConnectivityStatusSnapshot Snapshot { get; }
    event EventHandler? StatusChanged;
    Task RefreshAsync(CancellationToken ct = default);
}
