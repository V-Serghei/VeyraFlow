using System.Buffers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Veyra.Infrastructure.Native.Interop;

internal static class VeyraCoreNative
{
    private const string LibraryName = "veyra_core";

    private delegate int NativeUtf8Writer(byte[]? output, ulong outputLen, out ulong written);

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
    private static extern int veyra_scan_directory_limited_utf8(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string rootPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string extensionsCsv,
        ulong maxReadBytesPerSec,
        byte[]? output,
        ulong outputLen,
        out ulong written);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int veyra_scan_directory_limited_v2_utf8(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string rootPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string extensionsCsv,
        ulong maxReadBytesPerSec,
        ulong maxIoOperationsPerSec,
        byte[]? output,
        ulong outputLen,
        out ulong written);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int veyra_store_file_blocks_utf8(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filePath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string storeRoot,
        uint chunkSize,
        byte[]? output,
        ulong outputLen,
        out ulong written);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern long veyra_restore_file_blocks_utf8(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string storeRoot,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string blocksJson,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string targetPath,
        int overwriteExisting);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int veyra_last_error_utf8(
        byte[]? output,
        ulong outputLen,
        out ulong written);

    public static string ScanDirectoryJson(
        string rootPath,
        IReadOnlyCollection<string> extensions,
        int maxReadBytesPerSecond = 0,
        int maxIoOperationsPerSecond = 0)
    {
        var csv = BuildExtensionsCsv(extensions);

        if (maxReadBytesPerSecond > 0 || maxIoOperationsPerSecond > 0)
        {
            var maxRead = (ulong)Math.Max(0, maxReadBytesPerSecond);
            var maxIops = (ulong)Math.Max(0, maxIoOperationsPerSecond);

            try
            {
                return ReadJsonResult(
                    (buffer, len, out written) =>
                        veyra_scan_directory_limited_v2_utf8(rootPath, csv, maxRead, maxIops, buffer, len, out written),
                    "Native limited directory scan failed");
            }
            catch (EntryPointNotFoundException)
            {
                if (maxReadBytesPerSecond > 0)
                {
                    return ReadJsonResult(
                        (buffer, len, out written) =>
                            veyra_scan_directory_limited_utf8(rootPath, csv, maxRead, buffer, len, out written),
                        "Native limited directory scan failed");
                }
            }
        }

        return ReadJsonResult(
            (buffer, len, out written) =>
                veyra_scan_directory_utf8(rootPath, csv, buffer, len, out written),
            "Native directory scan failed");
    }

    public static string StoreFileBlocksJson(string filePath, string storeRoot, int chunkSize)
    {
        var safeChunk = chunkSize <= 0 ? 64 * 1024 : chunkSize;
        var chunk = (uint)Math.Clamp(safeChunk, 4 * 1024, 4 * 1024 * 1024);

        return ReadJsonResult(
            (buffer, len, out written) =>
                veyra_store_file_blocks_utf8(filePath, storeRoot, chunk, buffer, len, out written),
            "Native block store failed");
    }

    public static long RestoreFileBlocks(string storeRoot, string blocksJson, string targetPath, bool overwriteExisting)
    {
        var status = veyra_restore_file_blocks_utf8(storeRoot, blocksJson, targetPath, overwriteExisting ? 1 : 0);
        if (status < 0)
            throw new InvalidOperationException($"Native block restore failed {GetLastError()}".Trim());

        return status;
    }
    private static string ReadJsonResult(NativeUtf8Writer invoker, string errorPrefix)
    {
        const int initialBufferSize = 8 * 1024 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent(initialBufferSize);

        try
        {
            while (true)
            {
                var status = invoker(buffer, (ulong)buffer.Length, out var written);

                if (status == 0)
                {
                    if (written == 0)
                        return "[]";

                    if (written > (ulong)buffer.Length || written > int.MaxValue)
                        throw new InvalidOperationException($"{errorPrefix}. Native returned invalid length.");

                    return Encoding.UTF8.GetString(buffer, 0, (int)written);
                }

                if (written > (ulong)buffer.Length && written <= int.MaxValue)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = ArrayPool<byte>.Shared.Rent((int)written);
                    continue;
                }

                throw new InvalidOperationException($"{errorPrefix} {GetLastError()}".Trim());
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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

