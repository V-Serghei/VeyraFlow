namespace Veyra.Application.Common.Results;

public readonly struct OperationResult
{
    public bool Success { get; }
    public string? Error { get; }

    private OperationResult(bool success, string? error) { Success = success; Error = error; }

    public static OperationResult Ok() => new(true, null);
    public static OperationResult Fail(string error) => new(false, error);
}

public readonly struct OperationResult<T>
{
    public bool Success { get; }
    public T? Value { get; }
    public string? Error { get; }

    private OperationResult(bool success, T? value, string? error)
    {
        Success = success;
        Value = value;
        Error = error;
    }

    public static OperationResult<T> Ok(T value) => new(true, value, null);
    public static OperationResult<T> Fail(string error) => new(false, default, error);
}
