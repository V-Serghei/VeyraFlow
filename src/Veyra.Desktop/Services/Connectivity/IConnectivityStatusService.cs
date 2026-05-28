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

    /// <summary>
    /// Controls whether the background loop probes the cloud endpoint.
    /// Disable in guest mode — local network is still checked, but no HTTP
    /// request is sent to the cloud API and the state is reported as Unknown.
    /// </summary>
    void SetCloudProbeEnabled(bool enabled);
}
