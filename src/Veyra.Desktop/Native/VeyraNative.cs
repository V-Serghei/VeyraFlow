using System;
using System.Runtime.InteropServices;

namespace Veyra.Desktop.Native;

public static class VeyraNative
{
    private const string LibraryName = "veyra_core";

    /// <summary>
    /// Computes BLAKE3 hash of the given data
    /// </summary>
    /// <returns>Length of hash written to output buffer, or -1 on error</returns>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int veyra_blake3_hash(
        byte[] data,
        int dataLen,
        byte[] output,
        int outputLen);

    /// <summary>
    /// Compresses data using zstd
    /// </summary>
    /// <returns>Length of compressed data, or -1 on error</returns>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int veyra_zstd_compress(
        byte[] data,
        int dataLen,
        byte[] output,
        int outputLen,
        int level);

    /// <summary>
    /// Decompresses zstd data
    /// </summary>
    /// <returns>Length of decompressed data, or -1 on error</returns>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int veyra_zstd_decompress(
        byte[] data,
        int dataLen,
        byte[] output,
        int outputLen);

    /// <summary>
    /// Gets version string
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr veyra_get_version();

    /// <summary>
    /// Frees a string returned by veyra_get_version
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void veyra_free_string(IntPtr s);

    /// <summary>
    /// Helper method to get version string
    /// </summary>
    public static string GetVersion()
    {
        var ptr = veyra_get_version();
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
