using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Veyra.Application;
using Veyra.Application.Abstractions.Sync;
using Veyra.Desktop.Services.Observability;
using Veyra.Desktop.Services.Scheduling;
using Veyra.Desktop.Services.Sync;
using Veyra.Domain.Observability;
using Veyra.Infrastructure.Data;
using Veyra.Infrastructure.Native;
using Veyra.Infrastructure.Sync;
using Veyra.Shared.Logging;

namespace Veyra.Desktop.CompositionRoot;

public static partial class DependencyInjection
{
    public static ServiceProvider BuildServiceProvider(string connectionString)
    {
        var cfg = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        var services = new ServiceCollection();

        var schedulerSettingsStore = new SnapshotSchedulerSettingsStore();
        var schedulerOverrides = schedulerSettingsStore.Load();
        var runtimeObservabilitySettingsStore = new RuntimeObservabilitySettingsStore();
        var runtimeObservability = new RuntimeObservabilityControlService(runtimeObservabilitySettingsStore);

        services.AddSingleton<IConfiguration>(cfg);
        services.AddVeyraLogging(cfg, runtimeObservability: runtimeObservability);

        services.AddApplication();
        services.AddInfrastructureData(connectionString);
        services.AddInfrastructureSync(cfg);
        services.AddInfrastructureNative(cfg);
        services.AddDesktopInfrastructureOverrides();

        var schedulerOptions = new SnapshotSchedulerOptions
        {
            Enabled = schedulerOverrides?.Enabled ?? (cfg.GetValue<bool?>("SnapshotScheduler:Enabled") ?? true),
            PollSeconds = cfg.GetValue<int?>("SnapshotScheduler:PollSeconds") ?? 30,
            IntervalMinutes = schedulerOverrides?.IntervalMinutes ?? (cfg.GetValue<int?>("SnapshotScheduler:IntervalMinutes") ?? 15),
            QuietHoursStartHour = schedulerOverrides?.QuietHoursStartHour ?? (cfg.GetValue<int?>("SnapshotScheduler:QuietHoursStartHour") ?? 0),
            QuietHoursEndHour = schedulerOverrides?.QuietHoursEndHour ?? (cfg.GetValue<int?>("SnapshotScheduler:QuietHoursEndHour") ?? 0),
            MaxReadBytesPerSecond = cfg.GetValue<int?>("SnapshotScheduler:MaxReadBytesPerSecond") ?? 0,
            MaxIoOperationsPerSecond = cfg.GetValue<int?>("SnapshotScheduler:MaxIoOperationsPerSecond") ?? 0,
            RetryCount = cfg.GetValue<int?>("SnapshotScheduler:RetryCount") ?? 2,
            RetryDelaySeconds = cfg.GetValue<int?>("SnapshotScheduler:RetryDelaySeconds") ?? 10,
            IntegrityEnabled = cfg.GetValue<bool?>("SnapshotScheduler:IntegrityEnabled") ?? true,
            IntegrityIntervalMinutes = cfg.GetValue<int?>("SnapshotScheduler:IntegrityIntervalMinutes") ?? 180,
            IntegrityRepairFromCloud = cfg.GetValue<bool?>("SnapshotScheduler:IntegrityRepairFromCloud") ?? false,
            IntegrityIssueSampleLimit = cfg.GetValue<int?>("SnapshotScheduler:IntegrityIssueSampleLimit") ?? 200
        };

        services.AddDesktopRuntimeServices(
            schedulerSettingsStore,
            runtimeObservabilitySettingsStore,
            runtimeObservability,
            schedulerOptions);
        services.AddDesktopPlatformServices();
        services.AddDesktopPageViewModels();
        services.AddDesktopWindowViewModels();
        services.AddDesktopViews();

        return services.BuildServiceProvider();
    }
}
