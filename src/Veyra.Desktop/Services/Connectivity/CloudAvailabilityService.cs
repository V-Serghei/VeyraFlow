using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.DTOs;
using Veyra.Desktop.Services.Connectivity.Models;

namespace Veyra.Desktop.Services.Connectivity;

public sealed class CloudAvailabilityService : ICloudAvailabilityService, IDisposable
{
    private static readonly TimeSpan BaseCircuitOpenDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxCircuitOpenDuration = TimeSpan.FromMinutes(15);

    private readonly IConnectivityStatusService _connectivity;
    private readonly ILogger<CloudAvailabilityService> _log;
    private readonly object _gate = new();
    private int _consecutiveConnectivityFailures;
    private DateTime? _circuitOpenUntilUtc;
    private string? _lastFailureReason;
    private CloudAvailabilityState? _manualState;

    public CloudAvailabilityService(
        IConnectivityStatusService connectivity,
        ILogger<CloudAvailabilityService> log)
    {
        _connectivity = connectivity;
        _log = log;
        _connectivity.StatusChanged += OnConnectivityStatusChanged;
    }

    public CloudAvailabilitySnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return BuildSnapshot(DateTime.UtcNow);
        }
    }

    public bool CanExecuteCloudOperations => !ShouldSkipCloudOperation(out _);

    public bool ShouldSkipCloudOperation(out string reason)
    {
        lock (_gate)
        {
            var snapshot = BuildSnapshot(DateTime.UtcNow);
            if (snapshot.CanExecuteCloudOperations)
            {
                reason = string.Empty;
                return false;
            }

            reason = snapshot.Reason ?? "Cloud operations are temporarily unavailable.";
            return true;
        }
    }

    public void ReportCloudSuccess()
    {
        lock (_gate)
        {
            _consecutiveConnectivityFailures = 0;
            _circuitOpenUntilUtc = null;
            _lastFailureReason = null;
            _manualState = CloudAvailabilityState.Online;
        }
    }

    public void ReportCloudFailure(Exception ex)
    {
        if (IsAuthorizationFailure(ex))
        {
            lock (_gate)
            {
                _manualState = CloudAvailabilityState.Unauthorized;
                _circuitOpenUntilUtc = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                _lastFailureReason = GetInnermostMessage(ex);
            }

            return;
        }

        if (!IsConnectivityFailure(ex))
            return;

        lock (_gate)
        {
            _consecutiveConnectivityFailures = Math.Max(1, _consecutiveConnectivityFailures + 1);
            var multiplier = Math.Min(8, Math.Pow(2, _consecutiveConnectivityFailures - 1));
            var openFor = TimeSpan.FromSeconds(Math.Min(
                MaxCircuitOpenDuration.TotalSeconds,
                BaseCircuitOpenDuration.TotalSeconds * multiplier));

            _circuitOpenUntilUtc = DateTime.UtcNow + openFor;
            _manualState = CloudAvailabilityState.Reconnecting;
            _lastFailureReason = GetInnermostMessage(ex);

            _log.LogWarning(
                ex,
                "Cloud availability circuit opened. Failures {Failures}. RetryAfterUtc {RetryAfterUtc}",
                _consecutiveConnectivityFailures,
                _circuitOpenUntilUtc);
        }
    }

    public void Dispose()
        => _connectivity.StatusChanged -= OnConnectivityStatusChanged;

    private void OnConnectivityStatusChanged(object? sender, EventArgs e)
    {
        if (_connectivity.Snapshot.State != ConnectivityState.Online)
            return;

        lock (_gate)
        {
            if (_circuitOpenUntilUtc is null && _manualState != CloudAvailabilityState.Reconnecting)
                return;

            _consecutiveConnectivityFailures = 0;
            _circuitOpenUntilUtc = null;
            _lastFailureReason = null;
            _manualState = CloudAvailabilityState.Online;
        }

        _log.LogInformation("Cloud availability circuit closed after successful health probe.");
    }

    private CloudAvailabilitySnapshot BuildSnapshot(DateTime nowUtc)
    {
        var connectivitySnapshot = _connectivity.Snapshot;
        var state = MapConnectivityState(connectivitySnapshot.State);
        var reason = connectivitySnapshot.Detail;
        DateTime? retryAfterUtc = null;

        if (_manualState == CloudAvailabilityState.Unauthorized)
        {
            if (_circuitOpenUntilUtc is { } authRetryAfter && authRetryAfter > nowUtc)
            {
                state = CloudAvailabilityState.Unauthorized;
                retryAfterUtc = authRetryAfter;
                reason = _lastFailureReason ?? "Cloud authorization is required.";
            }
            else
            {
                _manualState = null;
                _circuitOpenUntilUtc = null;
                state = MapConnectivityState(connectivitySnapshot.State);
                reason = connectivitySnapshot.Detail;
            }
        }
        else if (_circuitOpenUntilUtc is { } openUntil)
        {
            if (openUntil > nowUtc)
            {
                state = CloudAvailabilityState.Reconnecting;
                retryAfterUtc = openUntil;
                reason = _lastFailureReason ?? "Cloud endpoint is temporarily unavailable.";
            }
            else
            {
                _circuitOpenUntilUtc = null;
                _manualState = null;
            }
        }

        if (state == CloudAvailabilityState.Online && _manualState == CloudAvailabilityState.Online)
            reason = null;

        return new CloudAvailabilitySnapshot(state, nowUtc, retryAfterUtc, reason);
    }

    private static CloudAvailabilityState MapConnectivityState(ConnectivityState state)
        => state switch
        {
            ConnectivityState.Online => CloudAvailabilityState.Online,
            ConnectivityState.InternetUnavailable => CloudAvailabilityState.Offline,
            ConnectivityState.CloudUnavailable => CloudAvailabilityState.CloudUnavailable,
            _ => CloudAvailabilityState.Unknown
        };

    private static bool IsConnectivityFailure(Exception ex)
    {
        if (ex is IOException or TimeoutException or TaskCanceledException or HttpRequestException or SocketException)
            return true;

        return ex.InnerException is not null && IsConnectivityFailure(ex.InnerException);
    }

    private static bool IsAuthorizationFailure(Exception ex)
    {
        var message = ex.ToString();
        return message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
               || message.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
               || message.Contains("invalid session", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetInnermostMessage(Exception ex)
    {
        var current = ex;
        while (current.InnerException is not null)
            current = current.InnerException;

        return string.IsNullOrWhiteSpace(current.Message) ? ex.Message : current.Message;
    }
}
