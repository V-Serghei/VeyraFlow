namespace Veyra.Desktop.Services.Preview;

public sealed record NativeWordCompareLaunchResult(bool Success, string? ErrorMessage = null)
{
    public static NativeWordCompareLaunchResult Ok() => new(true, null);

    public static NativeWordCompareLaunchResult Fail(string? error)
        => new(false, string.IsNullOrWhiteSpace(error) ? "Unable to open Microsoft Word compare." : error);
}
