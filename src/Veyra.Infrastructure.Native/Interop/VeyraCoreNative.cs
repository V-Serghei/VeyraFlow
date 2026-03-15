using System.Buffers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Veyra.Infrastructure.Native;

namespace Veyra.Infrastructure.Native.Interop;

internal static class VeyraCoreNative
{
    private const string LibraryName = "veyra_core";

    internal const string EntryScanDirectory = "veyra_scan_directory_utf8";
    internal const string EntryScanDirectoryLimited = "veyra_scan_directory_limited_utf8";
    internal const string EntryScanDirectoryLimitedV2 = "veyra_scan_directory_limited_v2_utf8";
    internal const string EntryStoreFileBlocks = "veyra_store_file_blocks_utf8";
    internal const string EntryRestoreFileBlocks = "veyra_restore_file_blocks_utf8";
    internal const string EntryBuildTextDiff = "veyra_build_text_diff_utf8";
    internal const string EntryCompareSnapshotLinks = "veyra_compare_snapshot_links_utf8";
    internal const string EntryCompareRepositoryPaths = "veyra_compare_repository_paths_utf8";
    internal const string EntryPlanRepositoryVersions = "veyra_plan_repository_versions_utf8";
    internal const string EntryLastError = "veyra_last_error_utf8";

    private static readonly string[] RequiredEntrypoints =
    [
        EntryLastError,
        EntryScanDirectory,
        EntryStoreFileBlocks,
        EntryRestoreFileBlocks,
        EntryBuildTextDiff,
        EntryCompareSnapshotLinks,
        EntryCompareRepositoryPaths,
        EntryPlanRepositoryVersions
    ];

    private static readonly string[] OptionalEntrypoints =
    [
        EntryScanDirectoryLimited,
        EntryScanDirectoryLimitedV2
    ];

    private static readonly Lazy<NativeLibraryProbe> LibraryProbe = new(
        ProbeNativeLibrary,
        LazyThreadSafetyMode.ExecutionAndPublication);

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
    private static extern int veyra_build_text_diff_utf8(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string leftFilePath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string rightFilePath,
        uint maxLines,
        byte[]? output,
        ulong outputLen,
        out ulong written);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int veyra_zstd_compress(
        byte[] data,
        int dataLen,
        byte[] output,
        int outputLen,
        int level);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int veyra_zstd_decompress(
        byte[] data,
        int dataLen,
        byte[] output,
        int outputLen);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int veyra_compare_snapshot_links_utf8(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string currentStatesJson,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string previousStatesJson,
        byte[]? output,
        ulong outputLen,
        out ulong written);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int veyra_compare_repository_paths_utf8(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string currentStatesJson,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string baselineStatesJson,
        uint take,
        byte[]? output,
        ulong outputLen,
        out ulong written);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int veyra_plan_repository_versions_utf8(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string statesJson,
        byte[]? output,
        ulong outputLen,
        out ulong written);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int veyra_last_error_utf8(
        byte[]? output,
        ulong outputLen,
        out ulong written);

    internal static NativeRuntimeHealthReport ProbeRuntimeHealth()
    {
        var probe = LibraryProbe.Value;
        var candidatePaths = probe.CandidatePaths;

        if (probe.Handle == IntPtr.Zero)
        {
            return new NativeRuntimeHealthReport(
                IsLoaded: false,
                IsHealthy: false,
                SupportsScan: false,
                SupportsStoreFileBlocks: false,
                SupportsRestoreFileBlocks: false,
                SupportsTextDiff: false,
                SupportsSnapshotComparison: false,
                SupportsRepositoryPathComparison: false,
                SupportsVersionPlanning: false,
                LoadedPath: null,
                ErrorMessage: probe.LoadError ?? "Unable to load native veyra_core library.",
                MissingEntrypoints: RequiredEntrypoints,
                CandidatePaths: candidatePaths);
        }

        var missingEntrypoints = RequiredEntrypoints
            .Where(entrypoint => !probe.ExportedEntrypoints.Contains(entrypoint))
            .ToArray();

        var supportsScan = HasEntrypoint(probe, EntryScanDirectory);
        var supportsStore = HasEntrypoint(probe, EntryStoreFileBlocks);
        var supportsRestore = HasEntrypoint(probe, EntryRestoreFileBlocks);
        var supportsTextDiff = HasEntrypoint(probe, EntryBuildTextDiff);
        var supportsSnapshotComparison = HasEntrypoint(probe, EntryCompareSnapshotLinks);
        var supportsRepositoryPathComparison = HasEntrypoint(probe, EntryCompareRepositoryPaths);
        var supportsVersionPlanning = HasEntrypoint(probe, EntryPlanRepositoryVersions);

        var isHealthy = missingEntrypoints.Length == 0;
        string? runtimeCheckError = null;

        if (isHealthy)
        {
            try
            {
                _ = CompareSnapshotLinksJson("[]", "[]");
                _ = CompareRepositoryPathsJson("[]", "[]", 1);
                _ = PlanRepositoryVersionsJson("[]");
            }
            catch (Exception ex)
            {
                isHealthy = false;
                runtimeCheckError = ex.Message;
            }
        }

        return new NativeRuntimeHealthReport(
            IsLoaded: true,
            IsHealthy: isHealthy,
            SupportsScan: supportsScan,
            SupportsStoreFileBlocks: supportsStore,
            SupportsRestoreFileBlocks: supportsRestore,
            SupportsTextDiff: supportsTextDiff,
            SupportsSnapshotComparison: supportsSnapshotComparison,
            SupportsRepositoryPathComparison: supportsRepositoryPathComparison,
            SupportsVersionPlanning: supportsVersionPlanning,
            LoadedPath: probe.LoadedPath,
            ErrorMessage: probe.LoadError ?? runtimeCheckError,
            MissingEntrypoints: missingEntrypoints,
            CandidatePaths: candidatePaths);
    }

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

    public static string BuildTextDiffJson(string leftFilePath, string rightFilePath, int maxLines)
    {
        var normalizedMaxLines = (uint)Math.Clamp(maxLines, 200, 20_000);

        return ReadJsonResult(
            (buffer, len, out written) =>
                veyra_build_text_diff_utf8(leftFilePath, rightFilePath, normalizedMaxLines, buffer, len, out written),
            "Native text diff failed");
    }

    public static byte[] ZstdDecompress(byte[] compressedBytes, int expectedOutputLength)
    {
        if (compressedBytes is null || compressedBytes.Length == 0)
            return [];

        if (expectedOutputLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedOutputLength));

        var output = new byte[expectedOutputLength];
        var written = veyra_zstd_decompress(compressedBytes, compressedBytes.Length, output, output.Length);
        if (written < 0)
            throw new InvalidOperationException($"Native zstd decompress failed {GetLastError()}".Trim());

        if (written == output.Length)
            return output;

        return output[..written];
    }

    public static byte[] ZstdCompress(byte[] inputBytes, int level = 3)
    {
        if (inputBytes is null || inputBytes.Length == 0)
            return [];

        var bufferLength = Math.Max(256, inputBytes.Length + (inputBytes.Length / 8) + 256);

        while (true)
        {
            var output = ArrayPool<byte>.Shared.Rent(bufferLength);
            try
            {
                var written = veyra_zstd_compress(
                    inputBytes,
                    inputBytes.Length,
                    output,
                    output.Length,
                    level);

                if (written >= 0)
                    return output[..written];

                var error = GetLastError();
                if (!error.Contains("buffer too small", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Native zstd compress failed {error}".Trim());
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(output);
            }

            bufferLength *= 2;
        }
    }

    public static string CompareSnapshotLinksJson(string currentStatesJson, string previousStatesJson)
    {
        return ReadJsonResult(
            (buffer, len, out written) =>
                veyra_compare_snapshot_links_utf8(currentStatesJson, previousStatesJson, buffer, len, out written),
            "Native snapshot comparison failed");
    }

    public static string CompareRepositoryPathsJson(string currentStatesJson, string baselineStatesJson, int take)
    {
        var safeTake = (uint)Math.Clamp(take, 1, 5000);
        return ReadJsonResult(
            (buffer, len, out written) =>
                veyra_compare_repository_paths_utf8(currentStatesJson, baselineStatesJson, safeTake, buffer, len, out written),
            "Native repository path comparison failed");
    }

    public static string PlanRepositoryVersionsJson(string statesJson)
    {
        return ReadJsonResult(
            (buffer, len, out written) =>
                veyra_plan_repository_versions_utf8(statesJson, buffer, len, out written),
            "Native repository version planner failed");
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

        return LibraryProbe.Value.Handle;
    }

    private static NativeLibraryProbe ProbeNativeLibrary()
    {
        var candidates = BuildCandidates();
        var loadErrors = new List<string>();
        NativeLibraryProbe? bestPartial = null;
        var bestPartialMissingCount = int.MaxValue;

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
                continue;

            try
            {
                var handle = NativeLibrary.Load(candidate);
                var exports = ReadExportedEntrypoints(handle);
                var missingCount = CountMissingRequiredEntrypoints(exports);

                if (missingCount == 0)
                {
                    if (bestPartial is not null && bestPartial.Handle != IntPtr.Zero)
                        NativeLibrary.Free(bestPartial.Handle);

                    return new NativeLibraryProbe(
                        Handle: handle,
                        LoadedPath: candidate,
                        ExportedEntrypoints: exports,
                        CandidatePaths: candidates,
                        LoadError: null);
                }

                if (missingCount < bestPartialMissingCount)
                {
                    if (bestPartial is not null && bestPartial.Handle != IntPtr.Zero)
                        NativeLibrary.Free(bestPartial.Handle);

                    bestPartialMissingCount = missingCount;
                    bestPartial = new NativeLibraryProbe(
                        Handle: handle,
                        LoadedPath: candidate,
                        ExportedEntrypoints: exports,
                        CandidatePaths: candidates,
                        LoadError: $"Loaded partial native library (missing required entrypoints: {missingCount}).");
                }
                else
                {
                    NativeLibrary.Free(handle);
                }
            }
            catch (Exception ex)
            {
                loadErrors.Add($"{candidate}: {ex.Message}");
            }
        }

        if (bestPartial is not null)
        {
            var missingEntrypoints = RequiredEntrypoints
                .Where(entrypoint => !bestPartial.ExportedEntrypoints.Contains(entrypoint))
                .ToArray();

            var partialError =
                $"Loaded native library {bestPartial.LoadedPath} but it is missing required entrypoints: {string.Join(", ", missingEntrypoints)}.";
            var combinedError = loadErrors.Count == 0
                ? partialError
                : partialError + " Other candidates load errors: " + string.Join(" | ", loadErrors);

            return bestPartial with { LoadError = combinedError };
        }

        var missingHint = candidates.Count == 0
            ? "No native candidates were generated."
            : "Checked candidates but did not load veyra_core.";

        var errorMessage = loadErrors.Count > 0
            ? string.Join(" | ", loadErrors)
            : missingHint;

        return new NativeLibraryProbe(
            Handle: IntPtr.Zero,
            LoadedPath: null,
            ExportedEntrypoints: new HashSet<string>(StringComparer.Ordinal),
            CandidatePaths: candidates,
            LoadError: errorMessage);
    }

    private static int CountMissingRequiredEntrypoints(HashSet<string> exports)
        => RequiredEntrypoints.Count(entrypoint => !exports.Contains(entrypoint));

    private static HashSet<string> ReadExportedEntrypoints(IntPtr handle)
    {
        var allEntrypoints = RequiredEntrypoints
            .Concat(OptionalEntrypoints)
            .Distinct(StringComparer.Ordinal);

        var exports = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entrypoint in allEntrypoints)
        {
            if (NativeLibrary.TryGetExport(handle, entrypoint, out _))
                exports.Add(entrypoint);
        }

        return exports;
    }

    private static bool HasEntrypoint(NativeLibraryProbe probe, string entrypoint)
        => probe.ExportedEntrypoints.Contains(entrypoint);

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
            Path.Combine(baseDir, "runtimes", rid, "native", fileName)
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            list.Add(Path.Combine(baseDir, "runtimes", "win-x64", "native", fileName));
            list.Add(Path.Combine(baseDir, "runtimes", "win-arm64", "native", fileName));
        }

        list.Add(Path.Combine(baseDir, fileName));

        var envPath = Environment.GetEnvironmentVariable("VEYRA_CORE_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            var p = envPath.Trim();
            list.Add(Directory.Exists(p) ? Path.Combine(p, fileName) : p);
        }

        var repoRoots = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."))
        };

        var rustTargets = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "x86_64-pc-windows-msvc", "aarch64-pc-windows-msvc" }
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? new[] { "x86_64-apple-darwin", "aarch64-apple-darwin" }
                : new[] { "x86_64-unknown-linux-gnu", "aarch64-unknown-linux-gnu" };

        foreach (var root in repoRoots)
        {
            var targetRoot = Path.Combine(root, "native", "veyra_core", "target");

            foreach (var rustTarget in rustTargets)
            {
                list.Add(Path.Combine(targetRoot, rustTarget, "release", fileName));
            }

            list.Add(Path.Combine(targetRoot, "release", fileName));

            foreach (var rustTarget in rustTargets)
            {
                list.Add(Path.Combine(targetRoot, rustTarget, "debug", fileName));
            }

            list.Add(Path.Combine(targetRoot, "debug", fileName));
        }

        return list
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed record NativeLibraryProbe(
        IntPtr Handle,
        string? LoadedPath,
        HashSet<string> ExportedEntrypoints,
        IReadOnlyList<string> CandidatePaths,
        string? LoadError);
}

