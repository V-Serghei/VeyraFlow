namespace Veyra.Application.Common.Results;

public readonly struct OperationResult
{
    public bool Success { get; }
    public string? Error { get; }

    private OperationResult(bool success, string? error) { Success = success; Error = error; }

    public static OperationResult Ok() => new(true, null);
    public static OperationResult Fail(string error) => new(false, error);
}
