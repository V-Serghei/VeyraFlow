using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.DTOs;
using Veyra.Infrastructure.Data.Persistence;
using Veyra.Infrastructure.Data.Sync;

namespace Veyra.Infrastructure.Data.Tests;

public sealed class CloudRepositoryManagementServiceTests
{
    [Fact]
    public async Task GetOverview_ReturnsCloudOnlyRepository()
    {
        await using var scope = await SqliteDbScope.CreateAsync();
        var cloud = new FakeCloudSyncService
        {
            Repositories =
            [
                new CloudRepositoryHeaderDto(
                    42,
                    "cloud-only",
                    "remote repository",
                    1001,
                    new DateTime(2026, 05, 27, 10, 00, 00, DateTimeKind.Utc),
                    "snapshot",
                    "manual",
                    3,
                    4)
            ],
            Metrics = CreateMetrics()
        };

        var service = CreateService(scope.Db, cloud);

        var overview = await service.GetOverviewAsync();

        Assert.Equal(1, overview.CloudRepositoryCount);
        var repository = Assert.Single(overview.Repositories);
        Assert.Equal(42, repository.CloudRepositoryId);
        Assert.False(repository.HasLocalLink);
        Assert.Equal("cloud_only", repository.RestoreStatus);
        Assert.Equal("Online", overview.ConnectionState);
    }

    [Fact]
    public async Task BuildRestorePlan_UsesFullHistorySnapshotPackages()
    {
        await using var scope = await SqliteDbScope.CreateAsync();
        var cloud = new FakeCloudSyncService
        {
            SnapshotPackages =
            [
                CreatePackage(42, 1001, "S1", "a.txt", "sha256-a"),
                CreatePackage(42, 1002, "S2", "b.txt", "sha256-b")
            ]
        };
        var service = CreateService(scope.Db, cloud);

        var plan = await service.BuildRestorePlanAsync(new CloudRepositoryRestoreOptionsDto(
            42,
            null,
            "full_history",
            RestoreFullHistory: true,
            RestoreLatestSnapshotOnly: false,
            RestoreMetadataOnly: false,
            RelinkExistingLocalFolder: false,
            ConflictStrategy: "safe"));

        Assert.Equal(2, plan.SnapshotCount);
        Assert.Equal(2, plan.BlocksRequired);
        Assert.False(plan.HasLocalConflicts);
    }

    [Fact]
    public async Task QueueDeleteCloudRepository_RequiresExplicitConfirmation()
    {
        await using var scope = await SqliteDbScope.CreateAsync();
        var cloud = new FakeCloudSyncService();
        var service = CreateService(scope.Db, cloud);

        var queued = await service.QueueDeleteCloudRepositoryAsync(42, "delete");

        Assert.Equal("failed", queued.Status);
        Assert.False(cloud.DeleteCalled);
    }

    private static CloudRepositoryManagementService CreateService(
        VeyraDbContext db,
        FakeCloudSyncService cloud)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRepositoryCloudSyncOrchestrator, FakeRepositoryCloudSyncOrchestrator>();
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:BlockStorePath"] = Path.Combine(Path.GetTempPath(), "veyra-cloud-manager-tests", Guid.NewGuid().ToString("N"))
            })
            .Build();

        return new CloudRepositoryManagementService(
            db,
            cloud,
            new FakeAuthService(),
            new FakeUserProfileRepository(),
            new FakeAccessTokenPolicyService(),
            new FakeCloudAvailabilityService(),
            new CloudRepositoryOperationTracker(),
            scopeFactory,
            configuration,
            NullLogger<CloudRepositoryManagementService>.Instance);
    }

    private static CloudSnapshotPackageDto CreatePackage(
        int repositoryId,
        long snapshotId,
        string title,
        string relativePath,
        string blockHash)
        => new(
            new CloudRepositoryMetadataDto(repositoryId, "cloud-only", null),
            new CloudSnapshotMetadataDto(snapshotId, title, "manual", DateTime.UtcNow.AddMinutes(snapshotId % 10), 1, 1, 0, 8, null),
            [new CloudSnapshotEntryDto(relativePath, null, Path.GetFileName(relativePath), false, Path.GetExtension(relativePath), 8, DateTime.UtcNow, "hash")],
            [new CloudFileVersionDto(relativePath, snapshotId, "hash", 8, false, DateTime.UtcNow, [new CloudBlockRefDto(0, blockHash, 8, 8)])]);

    private static CloudStorageMetricsDto CreateMetrics()
        => new(
            true,
            new CloudStorageSummaryDto(7, 2048, 7, 1024, 0, 0, 0),
            new CloudStorageBlockMetricsDto(7, 0, 7, 0, 2048, 0, 1024, 0),
            new CloudStoragePackMetricsDto(0, 0, 0, 0, 0),
            new CloudStorageFilesystemStatsDto(0, 7, 0, 0, 1024, 0, 1024));

    private sealed class SqliteDbScope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private SqliteDbScope(SqliteConnection connection, VeyraDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public VeyraDbContext Db { get; }

        public static async Task<SqliteDbScope> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<VeyraDbContext>()
                .UseSqlite(connection)
                .Options;

            var db = new VeyraDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new SqliteDbScope(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FakeCloudSyncService : ICloudSyncService
    {
        public IReadOnlyList<CloudRepositoryHeaderDto> Repositories { get; init; } = [];
        public IReadOnlyList<CloudSnapshotPackageDto> SnapshotPackages { get; init; } = [];
        public CloudStorageMetricsDto? Metrics { get; init; }
        public bool DeleteCalled { get; private set; }

        public Task<IReadOnlyList<CloudRepositoryHeaderDto>> GetRepositoriesAsync(string accessToken, CancellationToken ct = default)
            => Task.FromResult(Repositories);

        public Task<CloudSnapshotPackageDto?> GetLatestSnapshotAsync(string accessToken, int repositoryId, CancellationToken ct = default)
            => Task.FromResult<CloudSnapshotPackageDto?>(null);

        public Task<IReadOnlyList<CloudSnapshotPackageDto>> GetRepositorySnapshotsAsync(string accessToken, int repositoryId, CancellationToken ct = default)
            => Task.FromResult(SnapshotPackages);

        public Task<CloudPushResultDto?> PushSnapshotAsync(string accessToken, int repositoryId, CloudSnapshotPackageDto package, string? idempotencyKey = null, CancellationToken ct = default)
            => Task.FromResult<CloudPushResultDto?>(null);

        public Task<bool> BlockExistsAsync(string accessToken, string blockHash, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task UploadBlockAsync(string accessToken, string blockHash, Stream content, long? contentLength = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> DownloadBlockToFileAsync(string accessToken, string blockHash, string targetPath, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<CloudStorageMetricsDto?> GetStorageMetricsAsync(string accessToken, CancellationToken ct = default)
            => Task.FromResult(Metrics);

        public Task<CloudStorageRepairResultDto?> RepairStorageAsync(string accessToken, int scanLimit = 512, int compactLimit = 128, CancellationToken ct = default)
            => Task.FromResult<CloudStorageRepairResultDto?>(null);

        public Task<bool> DeleteRepositoryAsync(string accessToken, int repositoryId, CancellationToken ct = default)
        {
            DeleteCalled = true;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeCloudAvailabilityService : ICloudAvailabilityService
    {
        public CloudAvailabilitySnapshot Snapshot { get; } = new(CloudAvailabilityState.Online, DateTime.UtcNow, null);

        public bool CanExecuteCloudOperations => true;

        public bool ShouldSkipCloudOperation(out string reason)
        {
            reason = string.Empty;
            return false;
        }

        public void ReportCloudSuccess()
        {
        }

        public void ReportCloudFailure(Exception ex)
        {
        }
    }

    private sealed class FakeUserProfileRepository : IUserProfileRepository
    {
        private readonly UserProfileSessionDto _profile = new(
            "tester",
            "tester@example.com",
            1,
            1,
            "access-token",
            DateTime.UtcNow.AddHours(1),
            "refresh-token",
            DateTime.UtcNow.AddDays(7),
            false,
            DateTime.UtcNow);

        public Task<string?> GetActiveUsernameAsync(CancellationToken ct = default)
            => Task.FromResult<string?>(_profile.Username);

        public Task<UserProfileSessionDto?> GetActiveProfileAsync(CancellationToken ct = default)
            => Task.FromResult<UserProfileSessionDto?>(_profile);

        public Task<IReadOnlyList<UserProfileSessionDto>> GetProfilesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UserProfileSessionDto>>([_profile]);

        public Task SaveOrUpdateProfileAsync(string username, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SaveOrUpdateProfileAsync(
            string username,
            long? cloudUserId,
            string? accessToken,
            string? email,
            long? cloudSessionId,
            string? refreshToken,
            DateTime? accessTokenExpiresAtUtc,
            DateTime? refreshTokenExpiresAtUtc,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> SetActiveProfileAsync(string username, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> SetRequirePasswordForSensitiveActionsAsync(bool enabled, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task SignOutActiveAsync(CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeAccessTokenPolicyService : IAccessTokenPolicyService
    {
        public AccessTokenPolicyEvaluationDto Evaluate(string? accessToken, DateTime? nowUtc = null)
            => new(
                AccessTokenValidityState.Valid,
                DateTime.UtcNow.AddHours(1),
                TimeSpan.FromHours(1),
                true,
                "valid");

        public string GetPolicySummary() => "test";
    }

    private sealed class FakeAuthService : IAuthService
    {
        public Task<AuthSessionDto?> LoginAsync(string username, string password, CancellationToken ct = default)
            => Task.FromResult<AuthSessionDto?>(null);

        public Task<AuthSessionDto?> RegisterAsync(string username, string email, string password, CancellationToken ct = default)
            => Task.FromResult<AuthSessionDto?>(null);

        public Task<AuthSessionDto?> RefreshAsync(string refreshToken, CancellationToken ct = default)
            => Task.FromResult<AuthSessionDto?>(null);

        public Task<bool> LogoutAsync(string accessToken, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    private sealed class FakeRepositoryCloudSyncOrchestrator : IRepositoryCloudSyncOrchestrator
    {
        public Task TryPushLatestSnapshotAsync(int repositoryId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<int> RestoreRepositoriesFromCloudAsync(string? targetRootDirectory = null, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<bool> RestoreRepositoryFromCloudAsync(
            int cloudRepositoryId,
            string? targetRootDirectory = null,
            bool restoreFullHistory = false,
            bool restoreToAnotherFolder = false,
            CancellationToken ct = default)
            => Task.FromResult(false);

        public Task ProcessPendingQueueAsync(CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<RepositoryCloudRepairResultDto> RepairRepositoryCloudDataAsync(int repositoryId, CancellationToken ct = default)
            => Task.FromResult(new RepositoryCloudRepairResultDto(false, 0, 0, 0, 0, 0));

        public Task<bool> CancelRepositorySyncAsync(int repositoryId, CancellationToken ct = default)
            => Task.FromResult(false);
    }
}
