using System.Runtime.InteropServices;
using Excalibur.Core.Models;
using Excalibur.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace Excalibur.Core.Devices;

public sealed class DeviceEnumerator
{
    public IReadOnlyList<PhysicalDisk> EnumerateAll()
    {
        var list = new List<PhysicalDisk>();
        for (int i = 0; i < 32; i++)
        {
            var handle = NativeMethods.OpenDevice(i);
            if (handle.IsInvalid) continue;
            try
            {
                var disk = DescribeDisk(i, handle);
                if (disk != null) list.Add(disk);
            }
            finally { handle.Dispose(); }
        }
        return list;
    }

    private PhysicalDisk? DescribeDisk(int number, SafeFileHandle handle)
    {
        if (!QueryDeviceProperty(handle, out var desc)) return null;

        string? vendor = ReadAnsiString(handle, desc, desc.VendorIdOffset);
        string? product = ReadAnsiString(handle, desc, desc.ProductIdOffset);
        string? revision = ReadAnsiString(handle, desc, desc.ProductRevisionOffset);
        string? serial = ReadAnsiString(handle, desc, desc.SerialNumberOffset);

        var (lsize, psize) = QuerySectorSizes(handle);

        return new PhysicalDisk
        {
            DiskNumber = number,
            Path = $@"\\.\PhysicalDrive{number}",
            VendorId = vendor?.Trim(),
            ProductId = product?.Trim(),
            Model = (vendor != null && product != null)
                ? $"{vendor.Trim()} {product.Trim()}".Trim()
                : product?.Trim() ?? vendor?.Trim(),
            SerialNumber = serial?.Trim(),
            Firmware = revision?.Trim(),
            DeviceType = desc.DeviceType,
            RemovableMedia = desc.RemovableMedia != 0,
            CommandQueueing = desc.CommandQueueing != 0,
            BusTypeCode = desc.BusType,
            BusTypeName = Enum.IsDefined(typeof(StorageBusType), desc.BusType)
                ? ((StorageBusType)desc.BusType).ToString()
                : $"Unknown({desc.BusType})",
            LogicalSectorSize = lsize,
            PhysicalSectorSize = psize,
            CapacityBytes = QueryCapacity(handle),
            Volumes = QueryVolumes(number),
        };
    }

    private static bool QueryDeviceProperty(SafeFileHandle handle, out STORAGE_DEVICE_DESCRIPTOR desc)
    {
        desc = default;
        int qsize = Marshal.SizeOf<STORAGE_PROPERTY_QUERY>();
        IntPtr queryPtr = Marshal.AllocHGlobal(qsize);
        try
        {
            var q = new STORAGE_PROPERTY_QUERY
            {
                PropertyId = NativeMethods.StorageDeviceProperty,
                QueryType = 0,
                AdditionalParameters = new byte[1],
            };
            Marshal.StructureToPtr(q, queryPtr, false);

            const int bufSize = 1024;
            IntPtr bufPtr = Marshal.AllocHGlobal(bufSize);
            try
            {
                bool ok = NativeMethods.DeviceIoControl(handle,
                    NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY,
                    queryPtr, qsize,
                    bufPtr, bufSize,
                    out _, IntPtr.Zero);

                if (!ok) return false;
                desc = Marshal.PtrToStructure<STORAGE_DEVICE_DESCRIPTOR>(bufPtr);
                return true;
            }
            finally { Marshal.FreeHGlobal(bufPtr); }
        }
        finally { Marshal.FreeHGlobal(queryPtr); }
    }

    private static string? ReadAnsiString(SafeFileHandle handle, STORAGE_DEVICE_DESCRIPTOR desc, uint offset)
    {
        if (offset == 0) return null;

        int qsize = Marshal.SizeOf<STORAGE_PROPERTY_QUERY>();
        IntPtr queryPtr = Marshal.AllocHGlobal(qsize);
        const int bufSize = 4096;
        IntPtr bufPtr = Marshal.AllocHGlobal(bufSize);
        try
        {
            var q = new STORAGE_PROPERTY_QUERY
            {
                PropertyId = NativeMethods.StorageDeviceProperty,
                QueryType = 0,
                AdditionalParameters = new byte[1],
            };
            Marshal.StructureToPtr(q, queryPtr, false);

            bool ok = NativeMethods.DeviceIoControl(handle,
                NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY,
                queryPtr, qsize, bufPtr, bufSize, out _, IntPtr.Zero);
            if (!ok) return null;

            var baseDesc = Marshal.PtrToStructure<STORAGE_DEVICE_DESCRIPTOR>(bufPtr);
            if (offset >= baseDesc.Size) return null;

            IntPtr strPtr = IntPtr.Add(bufPtr, (int)offset);
            return Marshal.PtrToStringAnsi(strPtr)?.TrimEnd('\0');
        }
        finally
        {
            Marshal.FreeHGlobal(bufPtr);
            Marshal.FreeHGlobal(queryPtr);
        }
    }

    private static long? QueryCapacity(SafeFileHandle handle)
    {
        const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
        IntPtr outPtr = Marshal.AllocHGlobal(8);
        try
        {
            if (NativeMethods.DeviceIoControl(handle, IOCTL_DISK_GET_LENGTH_INFO,
                IntPtr.Zero, 0, outPtr, 8, out _, IntPtr.Zero))
            {
                return Marshal.ReadInt64(outPtr);
            }
        }
        finally { Marshal.FreeHGlobal(outPtr); }
        return null;
    }

    private static (uint? lsize, uint? psize) QuerySectorSizes(SafeFileHandle handle)
    {
        const int StorageAccessAlignmentProperty = 6;
        const uint IOCTL = NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY;

        int qsize = Marshal.SizeOf<STORAGE_PROPERTY_QUERY>();
        IntPtr qPtr = Marshal.AllocHGlobal(qsize);
        IntPtr bPtr = Marshal.AllocHGlobal(128);
        try
        {
            var q = new STORAGE_PROPERTY_QUERY
            {
                PropertyId = StorageAccessAlignmentProperty,
                QueryType = 0,
                AdditionalParameters = new byte[1],
            };
            Marshal.StructureToPtr(q, qPtr, false);
            if (NativeMethods.DeviceIoControl(handle, IOCTL, qPtr, qsize, bPtr, 128, out _, IntPtr.Zero))
            {
                uint bytesPerLogical = (uint)Marshal.ReadInt32(bPtr, 16);
                uint bytesPerPhysical = (uint)Marshal.ReadInt32(bPtr, 20);
                return (bytesPerLogical == 0 ? null : bytesPerLogical,
                        bytesPerPhysical == 0 ? null : bytesPerPhysical);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(bPtr);
            Marshal.FreeHGlobal(qPtr);
        }
        return (null, null);
    }

    private static List<VolumeInfo> QueryVolumes(int diskNumber)
    {
        var result = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType == DriveType.NoRootDirectory || !drive.IsReady) continue;
            var letter = drive.Name.TrimEnd('\\');
            if (string.IsNullOrEmpty(letter)) continue;

            var volHandle = NativeMethods.CreateFileW(
                $@"\\.\{letter}",
                NativeMethods.GENERIC_READ,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            if (volHandle.IsInvalid) continue;

            try
            {
                int bufSize = 8 + 24 * 32;   // header (ULONG + padding) + 32 extents
                IntPtr p = Marshal.AllocHGlobal(bufSize);
                try
                {
                    if (NativeMethods.DeviceIoControl(volHandle,
                        NativeMethods.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                        IntPtr.Zero, 0, p, bufSize, out _, IntPtr.Zero))
                    {
                        uint count = (uint)Marshal.ReadInt32(p, 0);
                        for (int e = 0; e < count; e++)
                        {
                            // DISK_EXTENT layout on x64 (with 8-byte alignment):
                            //   ULONG  DiskNumber;      offset 0  (4 bytes) + 4 pad
                            //   LONGLONG StartingOffset; offset 8
                            //   LONGLONG ExtentLength;   offset 16
                            // sizeof = 24. First extent starts at offset 8 (ULONG + 4 pad).
                            int baseOff = 8 + e * 24;
                            uint diskNum = (uint)Marshal.ReadInt32(p, baseOff);
                            long start = Marshal.ReadInt64(p, baseOff + 8);
                            long length = Marshal.ReadInt64(p, baseOff + 16);
                            if (diskNum == diskNumber)
                            {
                                result.Add(new VolumeInfo
                                {
                                    MountPoint = drive.Name,
                                    SizeBytes = length,
                                });
                                break;
                            }
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(p); }
            }
            finally { volHandle.Dispose(); }
        }
        return result;
    }
}