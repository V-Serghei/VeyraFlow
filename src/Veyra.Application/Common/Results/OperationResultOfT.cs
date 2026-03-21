namespace Veyra.Application.Common.Results;

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
