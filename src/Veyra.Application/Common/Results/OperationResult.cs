namespace Veyra.Application.Common.Results;

public readonly struct OperationResult
{
    public bool Success { get; }
    public string? Error { get; }
    private OperationErrorKind ErrorKind { get; }

    private OperationResult(bool success, string? error, OperationErrorKind kind)
    {
        Success = success;
        Error = error;
        ErrorKind = kind;
    }

    public static OperationResult Ok() => new(true, null, OperationErrorKind.None);

    public static OperationResult Fail(string error, OperationErrorKind kind = OperationErrorKind.General)
        => new(false, error, kind);

    public static OperationResult NetworkUnavailable(string? error = null)
        => new(false, error, OperationErrorKind.NetworkUnavailable);

    public static OperationResult CloudUnavailable(string? error = null)
        => new(false, error, OperationErrorKind.CloudUnavailable);

    public static OperationResult AuthRequired(string? error = null)
        => new(false, error, OperationErrorKind.AuthenticationRequired);

    public bool IsNetworkError => ErrorKind is OperationErrorKind.NetworkUnavailable
        or OperationErrorKind.CloudUnavailable
        or OperationErrorKind.Timeout;

    public bool IsAuthError => ErrorKind is OperationErrorKind.AuthenticationRequired
        or OperationErrorKind.TokenExpired
        or OperationErrorKind.Unauthorized;
}
