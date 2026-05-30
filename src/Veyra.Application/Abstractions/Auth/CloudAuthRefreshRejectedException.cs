using System.Net;

namespace Veyra.Application.Abstractions.Auth;

public sealed class CloudAuthRefreshRejectedException(
    HttpStatusCode statusCode,
    string? userMessage,
    string? responseBody)
    : Exception(BuildMessage(statusCode, userMessage, responseBody))
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string? UserMessage { get; } = userMessage;

    public string? ResponseBody { get; } = responseBody;

    private static string BuildMessage(HttpStatusCode statusCode, string? userMessage, string? responseBody)
    {
        string? detail = !string.IsNullOrWhiteSpace(userMessage)
            ? userMessage
            : responseBody;

        return string.IsNullOrWhiteSpace(detail)
            ? $"Cloud auth refresh was rejected with status {(int)statusCode}."
            : $"Cloud auth refresh was rejected with status {(int)statusCode}: {detail}";
    }
}
