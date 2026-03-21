using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Veyra.Desktop.Native;

public static class VeyraNative
{
    private const string Lib = "veyra_core";

    // ── BLAKE3 ────────────────────────────────────────────────

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int veyra_blake3_hash(
        byte[] data, int dataLen,
        byte[] output, int outputLen);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int veyra_blake3_hash_file(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        byte[] output, int outputLen);

    // ── Zstd ──────────────────────────────────────────────────

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int veyra_zstd_compress(
        byte[] data, int dataLen,
        byte[] output, int outputLen,
        int level);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int veyra_zstd_decompress(
        byte[] data, int dataLen,
        byte[] output, int outputLen);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern long veyra_zstd_compress_file(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string srcPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dstPath,
        int level);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern long veyra_zstd_decompress_file(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string srcPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dstPath);

    // ── Version ───────────────────────────────────────────────

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr veyra_get_version();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void veyra_free_string(IntPtr s);

    // ── Managed helpers ───────────────────────────────────────

    /// <summary>
    /// Compute BLAKE3 hash of a byte array. Returns 64-char hex string.
    /// </summary>
    public static string Blake3Hash(byte[] data)
    {
        var output = new byte[64];
        var len = veyra_blake3_hash(data, data.Length, output, output.Length);
        if (len < 0)
            throw new InvalidOperationException("BLAKE3 hash failed");
        return Encoding.ASCII.GetString(output, 0, len);
    }

    /// <summary>
    /// Compute BLAKE3 hash of a file by path. Returns 64-char hex string.
    /// </summary>
    public static string Blake3HashFile(string filePath)
    {
        var output = new byte[64];
        var len = veyra_blake3_hash_file(filePath, output, output.Length);
        if (len < 0)
            throw new InvalidOperationException($"BLAKE3 file hash failed for: {filePath}");
        return Encoding.ASCII.GetString(output, 0, len);
    }

    /// <summary>
    /// Compress file with zstd. Returns compressed size in bytes.
    /// </summary>
    public static long CompressFile(string srcPath, string dstPath, int level = 3)
    {
        var result = veyra_zstd_compress_file(srcPath, dstPath, level);
        if (result < 0)
            throw new InvalidOperationException($"Zstd compress failed: {srcPath}");
        return result;
    }

    /// <summary>
    /// Decompress zstd file. Returns decompressed size in bytes.
    /// </summary>
    public static long DecompressFile(string srcPath, string dstPath)
    {
        var result = veyra_zstd_decompress_file(srcPath, dstPath);
        if (result < 0)
            throw new InvalidOperationException($"Zstd decompress failed: {srcPath}");
        return result;
    }

    /// <summary>
    /// Get native library version string.
    /// </summary>
    public static string GetVersion()
    {
        var ptr = veyra_get_version();
        if (ptr == IntPtr.Zero)
            return "unknown";
        try
        {
            return Marshal.PtrToStringAnsi(ptr) ?? "unknown";
        }
        finally
        {
            veyra_free_string(ptr);
        }
    }
}
