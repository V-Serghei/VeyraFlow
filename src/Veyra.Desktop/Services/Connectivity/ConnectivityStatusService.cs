using System;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Veyra.Desktop.Services.Connectivity.Models;

namespace Veyra.Desktop.Services.Connectivity;

public sealed class ConnectivityStatusService : IConnectivityStatusService, IDisposable
{
    private static readonly TimeSpan OnlineRefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DegradedRefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UnknownRefreshInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ConnectivityStatusService> _log;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Uri? _cloudProbeUri;

    private volatile bool _cloudProbeEnabled = true;
    private ConnectivityStatusSnapshot _snapshot = new(ConnectivityState.Unknown, DateTime.UtcNow);

    public ConnectivityStatusService(
        IConfiguration config,
        IHttpClientFactory httpClientFactory,
        ILogger<ConnectivityStatusService> log)
    {
        _httpClientFactory = httpClientFactory;
        _log = log;

        var baseUrl = config["CloudApi:BaseUrl"]
                      ?? Environment.GetEnvironmentVariable("VEYRA_CLOUDAPI_URL")
                      ?? "http://localhost:8080";

        _cloudProbeUri = Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            ? new Uri(baseUri, "/healthz")
            : null;

        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        _ = Task.Run(MonitorLoopAsync);
    }

    public ConnectivityStatusSnapshot Snapshot => _snapshot;

    public event EventHandler? StatusChanged;

    public Task RefreshAsync(CancellationToken ct = default)
        => RefreshCoreAsync(ct, logFailures: false);

    public void SetCloudProbeEnabled(bool enabled)
    {
        _cloudProbeEnabled = enabled;
        if (!enabled)
        {
            var next = new ConnectivityStatusSnapshot(ConnectivityState.Unknown, DateTime.UtcNow);
            if (next != _snapshot)
            {
                _snapshot = next;
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        _ = Task.Run(() => RefreshCoreAsync(_shutdownCts.Token, logFailures: true));
    }

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _shutdownCts.Cancel();
        _shutdownCts.Dispose();
        _refreshGate.Dispose();
    }

    private async Task MonitorLoopAsync()
    {
        try
        {
            while (!_shutdownCts.IsCancellationRequested)
            {
                await RefreshCoreAsync(_shutdownCts.Token, logFailures: true).ConfigureAwait(false);
                await Task.Delay(ResolveRefreshInterval(_snapshot.State), _shutdownCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        try
        {
            await RefreshCoreAsync(_shutdownCts.Token, logFailures: true).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task RefreshCoreAsync(CancellationToken ct, bool logFailures)
    {
        if (_shutdownCts.IsCancellationRequested)
            return;

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = await ProbeAsync(ct).ConfigureAwait(false);
            if (next == _snapshot)
                return;

            _snapshot = next;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (logFailures)
                _log.LogDebug(ex, "Connectivity probe failed");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<ConnectivityStatusSnapshot> ProbeAsync(CancellationToken ct)
    {
        if (!_cloudProbeEnabled)
            return new(ConnectivityState.Unknown, DateTime.UtcNow);

        if (!NetworkInterface.GetIsNetworkAvailable())
            return new(ConnectivityState.InternetUnavailable, DateTime.UtcNow);

        if (_cloudProbeUri is null)
            return new(ConnectivityState.Online, DateTime.UtcNow);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ProbeTimeout);

            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, _cloudProbeUri);
            using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new(
                    ConnectivityState.CloudUnavailable,
                    DateTime.UtcNow,
                    $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim());
            }

            return new(ConnectivityState.Online, DateTime.UtcNow, ((int)response.StatusCode).ToString());
        }
        catch (Exception ex) when (TryClassify(ex, out var state))
        {
            return new(state, DateTime.UtcNow, ex.Message);
        }
    }

    private static bool TryClassify(Exception ex, out ConnectivityState state)
    {
        if (TryGetSocketException(ex) is { } socket)
        {
            state = socket.SocketErrorCode switch
            {
                SocketError.NetworkDown or
                SocketError.NetworkUnreachable or
                SocketError.HostDown or
                SocketError.HostUnreachable or
                SocketError.HostNotFound or
                SocketError.TryAgain => ConnectivityState.InternetUnavailable,
                _ => ConnectivityState.CloudUnavailable
            };
            return true;
        }

        if (ex is TaskCanceledException or TimeoutException)
        {
            state = ConnectivityState.CloudUnavailable;
            return true;
        }

        var message = ex.ToString();
        if (message.Contains("network is unreachable", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("no such host is known", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("name or service not known", StringComparison.OrdinalIgnoreCase))
        {
            state = ConnectivityState.InternetUnavailable;
            return true;
        }

        if (ex is HttpRequestException ||
            message.Contains("actively refused", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("connection refused", StringComparison.OrdinalIgnoreCase))
        {
            state = ConnectivityState.CloudUnavailable;
            return true;
        }

        state = ConnectivityState.Unknown;
        return false;
    }

    private static SocketException? TryGetSocketException(Exception ex)
    {
        Exception? current = ex;
        while (current is not null)
        {
            if (current is SocketException socket)
                return socket;

            current = current.InnerException;
        }

        return null;
    }

    private static TimeSpan ResolveRefreshInterval(ConnectivityState state)
        => state switch
        {
            ConnectivityState.Online => OnlineRefreshInterval,
            ConnectivityState.CloudUnavailable or ConnectivityState.InternetUnavailable => DegradedRefreshInterval,
            _ => UnknownRefreshInterval
        };
}
