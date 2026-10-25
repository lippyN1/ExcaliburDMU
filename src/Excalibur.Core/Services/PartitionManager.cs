using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Excalibur.Core.Native;

namespace Excalibur.Core.Services;

public static class PartitionManager
{
    public sealed class Result
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public string Output { get; init; } = "";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle hFile, IntPtr lpBuffer, uint nBytesToWrite, out uint lpBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    private const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    private const uint IOCTL_DISK_SET_DRIVE_LAYOUT_EX = 0x0007C054;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct MBR_PARTITION_ENTRY
    {
        public byte BootIndicator;
        public byte StartHead;
        public byte StartSector;
        public byte StartCylinder;
        public byte SystemID;
        public byte EndHead;
        public byte EndSector;
        public byte EndCylinder;
        public uint StartLBA;
        public uint SectorCount;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct MBR
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 446)]
        public byte[] BootCode;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public MBR_PARTITION_ENTRY[] Partitions;
        public ushort Signature;
    }

    public static Result QuickFormatDisk(int diskNumber, string fs)
    {
        try
        {
            // 1. Открываем диск и стираем старую MBR
            var handle = NativeMethods.CreateFileW(
                $@"\\.\PhysicalDrive{diskNumber}",
                NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle.IsInvalid)
                return new Result { Success = false, Error = $"Не удалось открыть диск. Win32Error={Marshal.GetLastWin32Error()}" };

            try
            {
                long diskSize = GetDiskSize(handle);
                if (diskSize <= 0) return new Result { Success = false, Error = "Не удалось определить размер." };

                IntPtr buffer = NativeMethods.VirtualAlloc(IntPtr.Zero, (UIntPtr)(10 * 1024 * 1024),
                    NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
                if (buffer == IntPtr.Zero) return new Result { Success = false, Error = "Ошибка памяти." };

                try
                {
                    for (int i = 0; i < 10 * 1024 * 1024; i++) Marshal.WriteByte(buffer, i, 0);
                    NativeMethods.SetFilePointerEx(handle, 0, out _, NativeMethods.FILE_BEGIN);
                    WriteFile(handle, buffer, 10 * 1024 * 1024, out _, IntPtr.Zero);
                }
                finally { NativeMethods.VirtualFree(buffer, UIntPtr.Zero, NativeMethods.MEM_RELEASE); }

                // 2. Создаем новую MBR
                uint totalSectors = (uint)(diskSize / 512);
                MBR mbr = new MBR { BootCode = new byte[446], Partitions = new MBR_PARTITION_ENTRY[4], Signature = 0xAA55 };
                mbr.Partitions[0] = new MBR_PARTITION_ENTRY
                {
                    BootIndicator = 0x00,
                    StartHead = 0xFE, StartSector = 0xFF, StartCylinder = 0xFF,
                    SystemID = 0x07, // NTFS
                    EndHead = 0xFE, EndSector = 0xFF, EndCylinder = 0xFF,
                    StartLBA = 2048,
                    SectorCount = totalSectors - 2048
                };

                IntPtr mbrPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MBR>());
                try
                {
                    Marshal.StructureToPtr(mbr, mbrPtr, false);
                    NativeMethods.SetFilePointerEx(handle, 0, out _, NativeMethods.FILE_BEGIN);
                    WriteFile(handle, mbrPtr, (uint)Marshal.SizeOf<MBR>(), out _, IntPtr.Zero);
                }
                finally { Marshal.FreeHGlobal(mbrPtr); }

                DeviceIoControl(handle, IOCTL_DISK_SET_DRIVE_LAYOUT_EX, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            }
            finally { handle.Dispose(); }

            // 3. Ждем, пока Windows присвоит букву
            System.Threading.Thread.Sleep(3000);

            // 4. Ищем букву
            char driveLetter = FindDriveLetter(diskNumber);
            if (driveLetter == '\0')
                return new Result { Success = false, Error = "Диск размечен, но Windows не присвоил букву." };

            // 5. Форматируем через format.com
            string args = $"{driveLetter}: /FS:{fs} /Q /Y";
            var psi = new ProcessStartInfo
            {
                FileName = "format.com",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };

            using var process = Process.Start(psi);
            if (process == null)
                return new Result { Success = false, Error = "Не удалось запустить format.com" };

            string output = process.StandardOutput.ReadToEnd();
            string err = process.StandardError.ReadToEnd();
            process.WaitForExit(60000);

            if (process.ExitCode != 0)
                return new Result { Success = false, Error = $"format.com вернул код {process.ExitCode}. {err} {output}" };

            return new Result { Success = true, Output = $"Диск {driveLetter}: отформатирован в {fs}." };
        }
        catch (Exception ex)
        {
            return new Result { Success = false, Error = ex.Message };
        }
    }

    private static long GetDiskSize(SafeFileHandle handle)
    {
        IntPtr outPtr = Marshal.AllocHGlobal(8);
        try
        {
            return DeviceIoControl(handle, IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0, outPtr, 8, out _, IntPtr.Zero)
                ? Marshal.ReadInt64(outPtr) : 0;
        }
        finally { Marshal.FreeHGlobal(outPtr); }
    }

    private static char FindDriveLetter(int diskNumber)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_LogicalDiskToPartition");
            foreach (ManagementObject queryObj in searcher.Get())
            {
                var antecedent = queryObj["Antecedent"]?.ToString() ?? "";
                var dependent = queryObj["Dependent"]?.ToString() ?? "";

                if (antecedent.Contains($"Disk #{diskNumber},"))
                {
                    int quoteIdx = dependent.IndexOf('"');
                    if (quoteIdx != -1 && dependent.Length > quoteIdx + 1)
                        return dependent[quoteIdx + 1];
                }
            }
        }
        catch { }
        return '\0';
    }
}
