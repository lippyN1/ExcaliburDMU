using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Excalibur.Core.Native;

internal static class RawDiskWriter
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(
        SafeFileHandle hFile,
        IntPtr lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    /// <summary>
    /// Write <paramref name="data"/> at the given byte offset (must be aligned to sector size). 
    /// Uses FILE_FLAG_NO_BUFFERING; buffer must be aligned (VirtualAlloc is page-aligned).
    /// </summary>
    public static bool WriteAt(SafeFileHandle handle, long byteOffset, byte[] data)
    {
        if (data.Length == 0) return true;

        IntPtr buf = NativeMethods.VirtualAlloc(IntPtr.Zero, (UIntPtr)data.Length,
            NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
        if (buf == IntPtr.Zero) return false;

        try
        {
            Marshal.Copy(data, 0, buf, data.Length);

            if (!NativeMethods.SetFilePointerEx(handle, byteOffset, out _, NativeMethods.FILE_BEGIN))
                return false;

            bool ok = WriteFile(handle, buf, (uint)data.Length, out uint written, IntPtr.Zero);
            return ok && written == (uint)data.Length;
        }
        finally
        {
            NativeMethods.VirtualFree(buf, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
        }
    }

    /// <summary>
    /// Write <paramref name="size"/> zeros at the given byte offset. Fast path for Erase mode.
    /// </summary>
    public static bool WriteZerosAt(SafeFileHandle handle, long byteOffset, int size)
    {
        IntPtr buf = NativeMethods.VirtualAlloc(IntPtr.Zero, (UIntPtr)size,
            NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
        if (buf == IntPtr.Zero) return false;

        try
        {
            // VirtualAlloc already zeroes memory, but be explicit
            for (int i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);

            if (!NativeMethods.SetFilePointerEx(handle, byteOffset, out _, NativeMethods.FILE_BEGIN))
                return false;

            bool ok = WriteFile(handle, buf, (uint)size, out uint written, IntPtr.Zero);
            return ok && written == (uint)size;
        }
        finally
        {
            NativeMethods.VirtualFree(buf, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
        }
    }
}