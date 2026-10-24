using System.Runtime.InteropServices;
using Excalibur.Core.Models;
using Excalibur.Core.Native;

namespace Excalibur.Core.Devices;

public static class PartitionTableBackup
{
    private const int SECTOR_SIZE = 512;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
        IntPtr lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    public static DiskLayoutBackup Capture(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int diskNumber, long sectorSize)
    {
        if (sectorSize <= 0) sectorSize = SECTOR_SIZE;

        long totalSectors = GetDiskSize(handle) / sectorSize;
        if (totalSectors <= 0) throw new InvalidOperationException("Cannot determine disk size");

        byte[] mbr = ReadSectors(handle, 0, 1, (int)sectorSize);
        byte[]? gptHeader = null;
        byte[]? gptEntries = null;
        byte[]? gptBackupHeader = null;
        byte[]? gptBackupEntries = null;

        bool looksLikeGpt = mbr.Length >= 512 &&
                            (mbr[0x1BE + 4] == 0xEE || mbr[0x1BE + 4] == 0xEF);

        byte[] lba1 = ReadSectors(handle, 1, 1, (int)sectorSize);
        bool gptSignature = lba1.Length >= 8 &&
                            lba1[0] == (byte)'E' && lba1[1] == (byte)'F' && lba1[2] == (byte)'I' &&
                            lba1[3] == (byte)' ' && lba1[4] == (byte)'P' && lba1[5] == (byte)'A' &&
                            lba1[6] == (byte)'R' && lba1[7] == (byte)'T';

        if (looksLikeGpt || gptSignature)
        {
            gptHeader = lba1;
            gptEntries = ReadSectors(handle, 2, 32, (int)sectorSize);
            if (totalSectors > 34)
            {
                gptBackupHeader = ReadSectors(handle, totalSectors - 1, 1, (int)sectorSize);
                gptBackupEntries = ReadSectors(handle, totalSectors - 33, 32, (int)sectorSize);
            }
        }

        var vbrs = new Dictionary<long, byte[]>();
        if (mbr.Length >= 512)
        {
            for (int i = 0; i < 4; i++)
            {
                int off = 0x1BE + i * 16;
                if (off + 16 > mbr.Length) break;
                if (mbr[off + 4] == 0) continue;

                uint lbaStart = (uint)(mbr[off + 8] | (mbr[off + 9] << 8) | (mbr[off + 10] << 16) | (mbr[off + 11] << 24));
                if (lbaStart == 0 || lbaStart >= totalSectors) continue;

                byte[] vbr = ReadSectors(handle, lbaStart, 1, (int)sectorSize);
                if (vbr.Length >= 512 && vbr[510] == 0x55 && vbr[511] == 0xAA)
                    vbrs[lbaStart] = vbr;
            }
        }

        return new DiskLayoutBackup
        {
            DiskNumber = diskNumber,
            SectorSize = sectorSize,
            TotalSectors = totalSectors,
            Mbr = mbr,
            GptPrimaryHeader = gptHeader,
            GptPartitionEntries = gptEntries,
            GptBackupHeader = gptBackupHeader,
            GptBackupEntries = gptBackupEntries,
            VbrSectors = vbrs,
        };
    }

    public static void Restore(Microsoft.Win32.SafeHandles.SafeFileHandle handle, DiskLayoutBackup backup)
    {
        if (backup.Mbr.Length > 0)
            WriteSectors(handle, 0, backup.Mbr);

        if (backup.GptPrimaryHeader != null)
            WriteSectors(handle, 1, backup.GptPrimaryHeader);
        if (backup.GptPartitionEntries != null)
            WriteSectors(handle, 2, backup.GptPartitionEntries);

        if (backup.GptBackupHeader != null)
            WriteSectors(handle, backup.TotalSectors - 1, backup.GptBackupHeader);
        if (backup.GptBackupEntries != null)
            WriteSectors(handle, backup.TotalSectors - 33, backup.GptBackupEntries);

        foreach (var kv in backup.VbrSectors)
            WriteSectors(handle, kv.Key, kv.Value);
    }

    private static byte[] ReadSectors(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long startLba, int count, int sectorSize)
    {
        int size = count * sectorSize;
        IntPtr buf = NativeMethods.VirtualAlloc(IntPtr.Zero, (UIntPtr)size,
            NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
        if (buf == IntPtr.Zero) throw new IOException("VirtualAlloc failed");
        try
        {
            if (!NativeMethods.SetFilePointerEx(handle, startLba * sectorSize, out _, NativeMethods.FILE_BEGIN))
                throw new IOException("SetFilePointerEx failed, Win32Error=" + Marshal.GetLastWin32Error());

            if (!NativeMethods.ReadFile(handle, buf, (uint)size, out uint br, IntPtr.Zero) || br != (uint)size)
                throw new IOException("ReadFile failed, Win32Error=" + Marshal.GetLastWin32Error());

            byte[] result = new byte[size];
            Marshal.Copy(buf, result, 0, size);
            return result;
        }
        finally
        {
            NativeMethods.VirtualFree(buf, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
        }
    }

    private static void WriteSectors(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long startLba, byte[] data)
    {
        int size = data.Length;
        IntPtr buf = NativeMethods.VirtualAlloc(IntPtr.Zero, (UIntPtr)size,
            NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
        if (buf == IntPtr.Zero) throw new IOException("VirtualAlloc failed");
        try
        {
            Marshal.Copy(data, 0, buf, size);

            if (!NativeMethods.SetFilePointerEx(handle, startLba * SECTOR_SIZE, out _, NativeMethods.FILE_BEGIN))
                throw new IOException("SetFilePointerEx failed, Win32Error=" + Marshal.GetLastWin32Error());

            if (!WriteFile(handle, buf, (uint)size, out uint bw, IntPtr.Zero) || bw != (uint)size)
                throw new IOException("WriteFile failed, Win32Error=" + Marshal.GetLastWin32Error());
        }
        finally
        {
            NativeMethods.VirtualFree(buf, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
        }
    }

    private static long GetDiskSize(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
        IntPtr outPtr = Marshal.AllocHGlobal(8);
        try
        {
            if (NativeMethods.DeviceIoControl(handle, IOCTL_DISK_GET_LENGTH_INFO,
                IntPtr.Zero, 0, outPtr, 8, out _, IntPtr.Zero))
                return Marshal.ReadInt64(outPtr);
        }
        finally { Marshal.FreeHGlobal(outPtr); }
        return 0;
    }
}