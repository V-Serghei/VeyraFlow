using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Filters;
using Veyra.Domain.Observability;

namespace Veyra.Shared.Logging;

public static class LoggingSetup
{
    /// <summary>
    /// Configures Serilog for the application, setting up file logging with compact JSON formatting,
    /// console logging, and optional Seq integration. The minimum log level can be configured via
    /// the provided IConfiguration or environment variables. Additionally, it supports dynamic log
    /// level control based on the IRuntimeObservabilityState.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configuration"></param>
    /// <param name="baseDirectory"></param>
    /// <param name="runtimeObservability"></param>
    public static void AddVeyraLogging(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        string? baseDirectory = null,
        IRuntimeObservabilityState? runtimeObservability = null)
    {
        var logsDir = baseDirectory ?? AppContext.BaseDirectory;
        var filePath = Path.Combine(logsDir, "logs", "veyra-.clef");
        var seqUrl = configuration?["Seq:Url"]
                     ?? configuration?["Serilog:SeqUrl"]
                     ?? Environment.GetEnvironmentVariable("SEQ_URL")
                     ?? "http://localhost:5341";
        var minimumLevelText = configuration?["Serilog:MinimumLevel"] ?? "Debug";
        var minimumLevel = Enum.TryParse<LogEventLevel>(minimumLevelText, true, out var parsedLevel)
            ? parsedLevel
            : LogEventLevel.Debug;
        var efCommandLevelText = configuration?["Serilog:EfCommandLevel"] ?? "Warning";
        var efCommandLevel = Enum.TryParse<LogEventLevel>(efCommandLevelText, true, out var parsedEfCommandLevel)
            ? parsedEfCommandLevel
            : LogEventLevel.Warning;
        var appLevelSwitch = new LoggingLevelSwitch(
            runtimeObservability?.IsLoggingEnabled == false
                ? LogEventLevel.Fatal
                : minimumLevel);

        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Is(LogEventLevel.Warning)
            .MinimumLevel.Override("Veyra", appLevelSwitch)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", efCommandLevel)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Information)
            .Filter.ByExcluding(Matching.FromSource("LuckyPennySoftware.MediatR.License"))
            .Enrich.FromLogContext()
            .Enrich.WithProcessId()
            .Enrich.WithThreadId()
            .Enrich.WithMachineName()
            .Enrich.WithProperty("Application", "Veyra.Desktop")
            .WriteTo.File(new CompactJsonFormatter(),
                filePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .WriteTo.Console();

        if (runtimeObservability is not null)
        {
            loggerConfig = loggerConfig.Filter.ByExcluding(_ => !runtimeObservability.IsLoggingEnabled);
            runtimeObservability.StateChanged += snapshot =>
            {
                appLevelSwitch.MinimumLevel = snapshot.IsLoggingEnabled
                    ? minimumLevel
                    : LogEventLevel.Fatal;
            };
        }

        if (!string.IsNullOrWhiteSpace(seqUrl))
        {
            loggerConfig = loggerConfig.WriteTo.Seq(serverUrl: seqUrl, apiKey: null, restrictedToMinimumLevel: minimumLevel);
        }

        Log.Logger = loggerConfig.CreateLogger();

        Log.Information(
            "Serilog initialized. MinimumLevel {MinimumLevel}. EfCommandLevel {EfCommandLevel}. File sink {File}. Seq {SeqUrl}",
            minimumLevel,
            efCommandLevel,
            filePath,
            seqUrl);

        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.AddSerilog(Log.Logger, dispose: true);
        });
    }
}
