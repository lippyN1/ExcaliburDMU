using System.Text;
using Excalibur.Core.Native;

namespace Excalibur.Core.Services;

public sealed class DataRecoveryService
{
    public sealed class Options
    {
        public int DiskNumber { get; set; }
        public string OutDir { get; set; } = "C:\\Recovered";
        public int MaxFiles { get; set; } = 100;
        public long MinSize { get; set; } = 1024 * 10;
    }

    public sealed class Progress
    {
        public double Percent { get; init; }
        public int FoundFiles { get; init; }
    }

    public string Run(Options opts, Action<Progress> onProgress, CancellationToken ct)
    {
        Directory.CreateDirectory(opts.OutDir);
        int foundCount = 0;
        var signatures = new (byte[] Magic, string Ext)[]
        {
            (new byte[] { 0xFF, 0xD8, 0xFF }, ".jpg"),
            (new byte[] { 0x89, 0x50, 0x4E, 0x47 }, ".png"),
            (new byte[] { 0x25, 0x50, 0x44, 0x46 }, ".pdf"),
            (new byte[] { 0x50, 0x4B, 0x03, 0x04 }, ".zip")
        };

        using var handle = NativeMethods.OpenDeviceForRead(opts.DiskNumber);
        if (handle.IsInvalid) throw new IOException("Не удалось открыть диск (нужен админ).");

        long diskSize = GetDiskSize(handle);
        long offset = 0;
        int blockSize = 32 * 1024 * 1024;
        IntPtr buffer = NativeMethods.VirtualAlloc(IntPtr.Zero, (UIntPtr)blockSize, NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);

        try
        {
            while (offset < diskSize && foundCount < opts.MaxFiles && !ct.IsCancellationRequested)
            {
                if (!NativeMethods.SetFilePointerEx(handle, offset, out _, NativeMethods.FILE_BEGIN)) break;
                if (!NativeMethods.ReadFile(handle, buffer, (uint)blockSize, out uint bytesRead, IntPtr.Zero) || bytesRead == 0) break;

                byte[] data = new byte[bytesRead];
                System.Runtime.InteropServices.Marshal.Copy(buffer, data, 0, (int)bytesRead);

                foreach (var sig in signatures)
                {
                    int idx = 0;
                    while ((idx = FindBytes(data, sig.Magic, idx)) != -1)
                    {
                        if (foundCount >= opts.MaxFiles) break;
                        int extractSize = Math.Min(10 * 1024 * 1024, data.Length - idx);
                        if (extractSize >= opts.MinSize)
                        {
                            string name = $"file_{foundCount:D4}_{offset + idx}{sig.Ext}";
                            File.WriteAllBytes(Path.Combine(opts.OutDir, name), data[idx..(idx + extractSize)]);
                            foundCount++;
                            onProgress?.Invoke(new Progress { Percent = (double)(offset + idx) / diskSize * 100, FoundFiles = foundCount });
                        }
                        idx += sig.Magic.Length;
                    }
                }
                offset += bytesRead;
                onProgress?.Invoke(new Progress { Percent = (double)offset / diskSize * 100, FoundFiles = foundCount });
            }
        }
        finally { NativeMethods.VirtualFree(buffer, UIntPtr.Zero, NativeMethods.MEM_RELEASE); }
        return $"Найдено файлов: {foundCount}. Папка: {opts.OutDir}";
    }

    private static long GetDiskSize(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        const uint IOCTL = 0x0007405C;
        IntPtr outPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(8);
        try { return NativeMethods.DeviceIoControl(handle, IOCTL, IntPtr.Zero, 0, outPtr, 8, out _, IntPtr.Zero) ? System.Runtime.InteropServices.Marshal.ReadInt64(outPtr) : 0; }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(outPtr); }
    }

    private static int FindBytes(byte[] haystack, byte[] needle, int start)
    {
        for (int i = start; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }
}
