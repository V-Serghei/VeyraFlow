using System.Net;

namespace Veyra.Application.Abstractions.Auth;

public sealed class CloudAuthRefreshRejectedException : Exception
{
    public CloudAuthRefreshRejectedException(
        HttpStatusCode statusCode,
        string? userMessage,
        string? responseBody)
        : base(BuildMessage(statusCode, userMessage, responseBody))
    {
        StatusCode = statusCode;
        UserMessage = userMessage;
        ResponseBody = responseBody;
    }

    public HttpStatusCode StatusCode { get; }

    public string? UserMessage { get; }

    public string? ResponseBody { get; }

    private static string BuildMessage(HttpStatusCode statusCode, string? userMessage, string? responseBody)
    {
        var detail = !string.IsNullOrWhiteSpace(userMessage)
            ? userMessage
            : responseBody;

        return string.IsNullOrWhiteSpace(detail)
            ? $"Cloud auth refresh was rejected with status {(int)statusCode}."
            : $"Cloud auth refresh was rejected with status {(int)statusCode}: {detail}";
    }
}
