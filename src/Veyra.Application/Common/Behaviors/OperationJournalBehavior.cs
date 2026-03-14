using System.Linq;
using System.Reflection;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Observability;

namespace Veyra.Application.Common.Behaviors;

public sealed class OperationJournalBehavior<TRequest, TResponse>(
    IEnumerable<IOperationJournalService> journals,
    ILogger<OperationJournalBehavior<TRequest, TResponse>> log)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!ShouldJournal(request.GetType()))
            return await next();

        var requestType = request.GetType();
        var action = requestType.Name;
        var category = ResolveCategory(requestType);
        var repositoryId = TryExtractRepositoryId(request);
        var username = TryExtractString(request, "Username");

        try
        {
            await TryAppendAsync(
                "info",
                category,
                action,
                repositoryId,
                username,
                "Operation started.",
                details: null,
                cancellationToken);

            var response = await next();
            var (success, message) = ExtractOutcome(response);

            await TryAppendAsync(
                success ? "info" : "warning",
                category,
                action,
                repositoryId,
                username,
                message,
                details: null,
                cancellationToken);

            return response;
        }
        catch (Exception ex)
        {
            await TryAppendAsync(
                "error",
                category,
                action,
                repositoryId,
                username,
                $"Unhandled exception: {ex.Message}",
                ex.GetType().Name,
                cancellationToken);
            throw;
        }
    }

    private async Task TryAppendAsync(
        string level,
        string category,
        string action,
        int? repositoryId,
        string? username,
        string message,
        string? details,
        CancellationToken ct)
    {
        var journal = journals.FirstOrDefault();
        if (journal is null)
            return;

        try
        {
            await journal.AppendAsync(
                new Application.DTOs.OperationJournalEntryDto(
                    Id: 0,
                    OccurredAtUtc: DateTime.UtcNow,
                    Level: level,
                    Category: category,
                    Action: action,
                    RepositoryId: repositoryId,
                    Username: username,
                    Message: message,
                    Details: details),
                ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Operation journal append failed for {Action}", action);
        }
    }

    private static bool ShouldJournal(Type requestType)
    {
        var ns = requestType.Namespace ?? string.Empty;
        return ns.StartsWith("Veyra.Application.Commands.", StringComparison.Ordinal);
    }

    private static string ResolveCategory(Type requestType)
    {
        var ns = requestType.Namespace ?? string.Empty;
        var name = requestType.Name;

        if (ns.Contains(".Auth", StringComparison.Ordinal))
            return "auth";

        if (name.Contains("Snapshot", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Scan", StringComparison.OrdinalIgnoreCase))
            return "snapshot";

        if (name.Contains("Restore", StringComparison.OrdinalIgnoreCase))
            return "restore";

        if (name.Contains("Import", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Export", StringComparison.OrdinalIgnoreCase))
            return "bundle";

        if (name.Contains("Repair", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Reindex", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Relink", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Integrity", StringComparison.OrdinalIgnoreCase))
            return "recovery";

        if (name.Contains("Sync", StringComparison.OrdinalIgnoreCase))
            return "sync";

        if (name.Contains("Retention", StringComparison.OrdinalIgnoreCase))
            return "retention";

        return "command";
    }

    private static int? TryExtractRepositoryId(TRequest request)
    {
        var value = TryExtractNumeric(request, "RepositoryId");
        if (value is not null)
            return value;

        value = TryExtractNumeric(request, "repositoryId");
        return value;
    }

    private static int? TryExtractNumeric(object source, string propertyName)
    {
        var property = source.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        if (property is null)
            return null;

        var raw = property.GetValue(source);
        if (raw is null)
            return null;

        return raw switch
        {
            int v => v,
            long v when v >= int.MinValue && v <= int.MaxValue => (int)v,
            short v => v,
            _ => null
        };
    }

    private static string? TryExtractString(object source, string propertyName)
    {
        var property = source.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        var raw = property?.GetValue(source)?.ToString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private static (bool Success, string Message) ExtractOutcome(TResponse response)
    {
        var type = typeof(TResponse);

        var successProp = type.GetProperty("Success", BindingFlags.Public | BindingFlags.Instance);
        if (successProp?.PropertyType == typeof(bool))
        {
            var success = (bool)(successProp.GetValue(response) ?? false);
            var summary = type.GetProperty("Summary", BindingFlags.Public | BindingFlags.Instance)?
                .GetValue(response)?.ToString();
            var error = type.GetProperty("Error", BindingFlags.Public | BindingFlags.Instance)?
                .GetValue(response)?.ToString();

            if (success)
                return (true, string.IsNullOrWhiteSpace(summary) ? "Completed successfully." : summary!);

            return (false, string.IsNullOrWhiteSpace(error)
                ? (string.IsNullOrWhiteSpace(summary) ? "Completed with warnings." : summary!)
                : error!);
        }

        return (true, "Completed successfully.");
    }
}
