using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Veyra.Infrastructure.Native.Interop;

internal static class VeyraCoreNative
{
    private const string LibraryName = "veyra_core";

    static VeyraCoreNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(VeyraCoreNative).Assembly, ResolveLibrary);
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int veyra_scan_directory_utf8(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string rootPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string extensionsCsv,
        byte[]? output,
        ulong outputLen,
        out ulong written);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int veyra_last_error_utf8(
        byte[]? output,
        ulong outputLen,
        out ulong written);

    public static string ScanDirectoryJson(string rootPath, IReadOnlyCollection<string> extensions)
    {
        var csv = BuildExtensionsCsv(extensions);

        var status = veyra_scan_directory_utf8(rootPath, csv, null, 0, out var needed);
        if (status != 0)
            throw new InvalidOperationException($"Native directory scan failed {GetLastError()}".Trim());

        if (needed == 0)
            return "[]";

        if (needed > int.MaxValue)
            throw new InvalidOperationException("Native directory scan result is too large.");

        var buffer = new byte[(int)needed];

        status = veyra_scan_directory_utf8(rootPath, csv, buffer, (ulong)buffer.Length, out var written);
        if (status != 0)
            throw new InvalidOperationException($"Native directory scan failed {GetLastError()}".Trim());

        if (written > (ulong)buffer.Length)
            throw new InvalidOperationException("Native directory scan returned invalid length.");

        return Encoding.UTF8.GetString(buffer, 0, (int)written);
    }

    private static string BuildExtensionsCsv(IReadOnlyCollection<string> extensions)
    {
        if (extensions.Count == 0)
            return string.Empty;

        return string.Join(',', extensions
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim().ToLowerInvariant())
            .Select(v => v.StartsWith('.') ? v : "." + v)
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string GetLastError()
    {
        var status = veyra_last_error_utf8(null, 0, out var needed);
        if (status != 0 || needed == 0)
            return string.Empty;

        if (needed > int.MaxValue)
            return string.Empty;

        var buffer = new byte[(int)needed];
        status = veyra_last_error_utf8(buffer, (ulong)buffer.Length, out var written);

        if (status != 0 || written == 0)
            return string.Empty;

        if (written > (ulong)buffer.Length)
            return string.Empty;

        return Encoding.UTF8.GetString(buffer, 0, (int)written).Trim();
    }

    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals(LibraryName, StringComparison.Ordinal))
            return IntPtr.Zero;

        var candidates = BuildCandidates();
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
                continue;

            if (NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        return IntPtr.Zero;
    }

    private static IReadOnlyList<string> BuildCandidates()
    {
        var ext = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "dylib"
                : "so";

        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"{LibraryName}.{ext}"
            : $"lib{LibraryName}.{ext}";

        var baseDir = AppContext.BaseDirectory;
        var rid = RuntimeInformation.RuntimeIdentifier;

        var list = new List<string>
        {
            Path.Combine(baseDir, fileName),
            Path.Combine(baseDir, "runtimes", rid, "native", fileName)
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            list.Add(Path.Combine(baseDir, "runtimes", "win-x64", "native", fileName));

        var envPath = Environment.GetEnvironmentVariable("VEYRA_CORE_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            var p = envPath.Trim();
            list.Add(Directory.Exists(p) ? Path.Combine(p, fileName) : p);
        }

        var repoDevPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "native", "veyra_core", "target", "release", fileName));
        list.Add(repoDevPath);

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
