namespace Veyra.Application.Common.Results;

public readonly struct OperationResult<T>
{
    public bool Success { get; }
    public T? Value { get; }
    public string? Error { get; }
    public string? Summary { get; }
    private OperationErrorKind ErrorKind { get; }

    private OperationResult(bool success, T? value, string? error, string? summary, OperationErrorKind kind)
    {
        Success = success;
        Value = value;
        Error = error;
        Summary = summary;
        ErrorKind = kind;
    }

    public static OperationResult<T> Ok(T value, string? summary = null)
        => new(true, value, null, summary, OperationErrorKind.None);

    public static OperationResult<T> Fail(string error, OperationErrorKind kind = OperationErrorKind.General)
        => new(false, default, error, null, kind);

    public static OperationResult<T> NetworkUnavailable(string? error = null)
        => new(false, default, error, null, OperationErrorKind.NetworkUnavailable);

    public static OperationResult<T> CloudUnavailable(string? error = null)
        => new(false, default, error, null, OperationErrorKind.CloudUnavailable);

    public static OperationResult<T> AuthRequired(string? error = null)
        => new(false, default, error, null, OperationErrorKind.AuthenticationRequired);

    public bool IsNetworkError => ErrorKind is OperationErrorKind.NetworkUnavailable
        or OperationErrorKind.CloudUnavailable
        or OperationErrorKind.Timeout;

    public bool IsAuthError => ErrorKind is OperationErrorKind.AuthenticationRequired
        or OperationErrorKind.TokenExpired
        or OperationErrorKind.Unauthorized;
}
