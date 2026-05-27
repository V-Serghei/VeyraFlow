using System;
using Microsoft.Extensions.Configuration;

namespace Veyra.Infrastructure.Sync.Sync;

internal sealed class CloudSyncFaultInjectionOptions
{
    public bool Enabled { get; init; }
    public int NetworkDropEvery { get; init; }
    public int TimeoutEvery { get; init; }
    public bool DuplicateAckOnPush { get; init; }
    public bool StaleRemoteHeadOnList { get; init; }

    public static CloudSyncFaultInjectionOptions FromConfiguration(IConfiguration cfg)
    {
        var enabled = cfg.GetValue<bool?>("CloudSync:FaultInjection:Enabled") ?? false;

        return new CloudSyncFaultInjectionOptions
        {
            Enabled = enabled,
            NetworkDropEvery = enabled
                ? Math.Clamp(cfg.GetValue<int?>("CloudSync:FaultInjection:NetworkDropEvery") ?? 0, 0, 1000)
                : 0,
            TimeoutEvery = enabled
                ? Math.Clamp(cfg.GetValue<int?>("CloudSync:FaultInjection:TimeoutEvery") ?? 0, 0, 1000)
                : 0,
            DuplicateAckOnPush = enabled && (cfg.GetValue<bool?>("CloudSync:FaultInjection:DuplicateAckOnPush") ?? false),
            StaleRemoteHeadOnList = enabled && (cfg.GetValue<bool?>("CloudSync:FaultInjection:StaleRemoteHeadOnList") ?? false)
        };
    }
}
