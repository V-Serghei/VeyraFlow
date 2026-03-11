using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Veyra.Desktop.Services.Sync;

public sealed class RepositoryFsEventQueueService(
    ILogger<RepositoryFsEventQueueService> log)
    : IRepositoryFsEventQueueService
{
    private const string QueuePathOverrideEnvironmentVariable = "VEYRA_FS_EVENT_QUEUE_PATH";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task EnqueueAsync(int repositoryId, string fullPath, string eventKind, CancellationToken ct = default)
    {
        if (repositoryId <= 0 || string.IsNullOrWhiteSpace(fullPath))
            return;

        await _gate.WaitAsync(ct);
        try
        {
            var state = await LoadStateAsync(ct);
            var now = DateTime.UtcNow;
            var normalizedPath = fullPath.Trim();

            var existing = state.Items.FirstOrDefault(x =>
                x.RepositoryId == repositoryId
                && x.Status == FsEventStatus.Pending
                && string.Equals(x.FullPath, normalizedPath, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                state.Items.Add(new RepositoryFsEventQueueItem
                {
                    Id = state.NextId++,
                    RepositoryId = repositoryId,
                    FullPath = normalizedPath,
                    EventKind = NormalizeEventKind(eventKind),
                    Status = FsEventStatus.Pending,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    RetryCount = 0
                });
            }
            else
            {
                existing.EventKind = NormalizeEventKind(eventKind);
                existing.UpdatedAtUtc = now;
            }

            await SaveStateAsync(state, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RepositoryFsEventLease> LeaseAsync(
        int repositoryId,
        int maxItems,
        TimeSpan staleRunningAfter,
        CancellationToken ct = default)
    {
        if (repositoryId <= 0 || maxItems <= 0)
            return RepositoryFsEventLease.Empty;

        await _gate.WaitAsync(ct);
        try
        {
            var state = await LoadStateAsync(ct);
            var now = DateTime.UtcNow;
            var staleBefore = now - staleRunningAfter;

            foreach (var stale in state.Items.Where(x =>
                         x.RepositoryId == repositoryId
                         && x.Status == FsEventStatus.Running
                         && x.UpdatedAtUtc <= staleBefore))
            {
                stale.Status = FsEventStatus.Pending;
                stale.UpdatedAtUtc = now;
                stale.RetryCount++;
            }

            var lease = state.Items
                .Where(x => x.RepositoryId == repositoryId && x.Status == FsEventStatus.Pending)
                .OrderBy(x => x.CreatedAtUtc)
                .ThenBy(x => x.Id)
                .Take(maxItems)
                .ToList();

            if (lease.Count == 0)
            {
                await SaveStateAsync(state, ct);
                return RepositoryFsEventLease.Empty;
            }

            foreach (var item in lease)
            {
                item.Status = FsEventStatus.Running;
                item.UpdatedAtUtc = now;
            }

            await SaveStateAsync(state, ct);
            return new RepositoryFsEventLease(lease.Select(x => x.Id).ToArray(), lease.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteAsync(int repositoryId, IReadOnlyCollection<long> itemIds, CancellationToken ct = default)
    {
        if (repositoryId <= 0 || itemIds.Count == 0)
            return;

        await _gate.WaitAsync(ct);
        try
        {
            var state = await LoadStateAsync(ct);
            var ids = itemIds.ToHashSet();
            state.Items.RemoveAll(x => x.RepositoryId == repositoryId && ids.Contains(x.Id));
            await SaveStateAsync(state, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RequeueAsync(int repositoryId, IReadOnlyCollection<long> itemIds, string? error, CancellationToken ct = default)
    {
        if (repositoryId <= 0 || itemIds.Count == 0)
            return;

        await _gate.WaitAsync(ct);
        try
        {
            var state = await LoadStateAsync(ct);
            var now = DateTime.UtcNow;
            var ids = itemIds.ToHashSet();

            foreach (var item in state.Items.Where(x => x.RepositoryId == repositoryId && ids.Contains(x.Id)))
            {
                item.Status = FsEventStatus.Pending;
                item.UpdatedAtUtc = now;
                item.RetryCount++;
                item.LastError = string.IsNullOrWhiteSpace(error) ? null : error;
            }

            await SaveStateAsync(state, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> HasPendingAsync(int repositoryId, CancellationToken ct = default)
    {
        if (repositoryId <= 0)
            return false;

        await _gate.WaitAsync(ct);
        try
        {
            var state = await LoadStateAsync(ct);
            return state.Items.Any(x => x.RepositoryId == repositoryId && x.Status == FsEventStatus.Pending);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string ResolvePath()
    {
        var explicitPath = Environment.GetEnvironmentVariable(QueuePathOverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var resolved = Path.GetFullPath(explicitPath.Trim());
            var parent = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);

            return resolved;
        }

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeyraFlow",
            "state");

        Directory.CreateDirectory(root);
        return Path.Combine(root, "repository-fs-event-queue.json");
    }

    private async Task<RepositoryFsEventQueueState> LoadStateAsync(CancellationToken ct)
    {
        var path = ResolvePath();
        if (!File.Exists(path))
            return new RepositoryFsEventQueueState();

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                32 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var state = await JsonSerializer.DeserializeAsync<RepositoryFsEventQueueState>(stream, JsonOptions, ct);
            if (state is null)
                return new RepositoryFsEventQueueState();

            if (state.NextId <= 0)
                state.NextId = Math.Max(1, state.Items.DefaultIfEmpty().Max(x => x?.Id ?? 0) + 1);

            return state;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to read FS event queue. Reinitializing queue file.");
            return new RepositoryFsEventQueueState();
        }
    }

    private async Task SaveStateAsync(RepositoryFsEventQueueState state, CancellationToken ct)
    {
        state.NextId = Math.Max(state.NextId, Math.Max(1, state.Items.DefaultIfEmpty().Max(x => x?.Id ?? 0) + 1));
        var path = ResolvePath();
        var tempPath = path + ".tmp";

        await using (var stream = new FileStream(
                         tempPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         32 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, ct);
        }

        if (File.Exists(path))
            File.Delete(path);

        File.Move(tempPath, path);
    }

    private static string NormalizeEventKind(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "created" => "created",
            "deleted" => "deleted",
            "renamed" => "renamed",
            "error" => "error",
            _ => "changed"
        };
    }

    private sealed class RepositoryFsEventQueueState
    {
        public long NextId { get; set; } = 1;
        public List<RepositoryFsEventQueueItem> Items { get; set; } = [];
    }

    private sealed class RepositoryFsEventQueueItem
    {
        public long Id { get; set; }
        public int RepositoryId { get; set; }
        public string FullPath { get; set; } = string.Empty;
        public string EventKind { get; set; } = "changed";
        public FsEventStatus Status { get; set; } = FsEventStatus.Pending;
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public int RetryCount { get; set; }
        public string? LastError { get; set; }
    }

    private enum FsEventStatus
    {
        Pending = 0,
        Running = 1
    }
}
