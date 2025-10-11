using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Veyra.Shared.Logging;

public static class LoggingSetup
{
    public static void AddVeyraLogging(this IServiceCollection services, string? baseDirectory = null)
    {
        var logsDir = baseDirectory ?? AppContext.BaseDirectory;
        var filePath = Path.Combine(logsDir, "logs", "veyra-.clef");

        var seqUrl = Environment.GetEnvironmentVariable("SEQ_URL") ?? "http://localhost:5341";

        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Veyra", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .Enrich.WithProcessId()
            .Enrich.WithThreadId()
            .Enrich.WithMachineName()
            .WriteTo.File(new CompactJsonFormatter(),
                filePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .WriteTo.Console();

        if (!string.IsNullOrWhiteSpace(seqUrl))
        {
            loggerConfig = loggerConfig.WriteTo.Seq(serverUrl: seqUrl, apiKey: null, restrictedToMinimumLevel: LogEventLevel.Information);
        }

        Log.Logger = loggerConfig.CreateLogger();

        Log.Information("Serilog initialized. File sink: {File}, Seq: {SeqUrl}", filePath, seqUrl);

        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.AddSerilog(Log.Logger, dispose: true);
        });
    }
}
