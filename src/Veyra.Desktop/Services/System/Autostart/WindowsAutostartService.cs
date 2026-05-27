using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Veyra.Desktop.Services.System;

[SupportedOSPlatform("windows")]
public sealed class WindowsAutostartService : IWindowsAutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string DefaultEntryName = "VeyraFlow";

    public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public string EntryName => DefaultEntryName;

    public Task<bool> IsEnabledAsync(CancellationToken ct = default)
        => Task.FromResult(IsSupported && TryReadValue() is not null);

    public Task<string?> GetRegisteredCommandAsync(CancellationToken ct = default)
        => Task.FromResult(IsSupported ? TryReadValue() : null);

    public Task EnableAsync(CancellationToken ct = default)
    {
        if (!IsSupported)
            return Task.CompletedTask;

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key?.SetValue(EntryName, BuildLaunchCommand(), RegistryValueKind.String);
        return Task.CompletedTask;
    }

    public Task DisableAsync(CancellationToken ct = default)
    {
        if (!IsSupported)
            return Task.CompletedTask;

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(EntryName) is not null)
            key.DeleteValue(EntryName, throwOnMissingValue: false);

        return Task.CompletedTask;
    }

    private static string? TryReadValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(DefaultEntryName) as string;
    }

    private static string BuildLaunchCommand()
    {
        var processPath = Environment.ProcessPath;
        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;

        if (string.IsNullOrWhiteSpace(processPath))
            throw new InvalidOperationException("Cannot resolve current process path for autostart.");

        if (processPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(entryAssemblyPath) &&
            entryAssemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return $"\"{processPath}\" \"{entryAssemblyPath}\"";
        }

        return $"\"{processPath}\"";
    }
}
