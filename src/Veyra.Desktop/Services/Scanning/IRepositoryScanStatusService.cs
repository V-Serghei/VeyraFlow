using System;
using Veyra.Application.DTOs;

namespace Veyra.Desktop.Services.Scanning;

public interface IRepositoryScanStatusService
{
    event EventHandler<RepositoryScanStatusChangedEventArgs>? StatusChanged;

    RepositoryScanStatusSnapshot? GetSnapshot(int repositoryId);

    RepositoryScanStatusSnapshot Begin(int repositoryId, string trigger, string message);

    RepositoryScanStatusSnapshot Report(int repositoryId, string trigger, RepositoryScanProgressDto progress);

    RepositoryScanStatusSnapshot Complete(int repositoryId, string trigger, bool success, string? message = null);
}
