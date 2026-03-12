using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Filters;

namespace Veyra.Shared.Logging;

public static class LoggingSetup
{
    public static void AddVeyraLogging(this IServiceCollection services, IConfiguration? configuration = null, string? baseDirectory = null)
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

        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .MinimumLevel.Override("Veyra", minimumLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Information)
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

        if (!string.IsNullOrWhiteSpace(seqUrl))
        {
            loggerConfig = loggerConfig.WriteTo.Seq(serverUrl: seqUrl, apiKey: null, restrictedToMinimumLevel: minimumLevel);
        }

        Log.Logger = loggerConfig.CreateLogger();

        Log.Information(
            "Serilog initialized. MinimumLevel {MinimumLevel}. File sink {File}. Seq {SeqUrl}",
            minimumLevel,
            filePath,
            seqUrl);

        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.AddSerilog(Log.Logger, dispose: true);
        });
    }
}
