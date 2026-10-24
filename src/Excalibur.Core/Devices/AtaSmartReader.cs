using System.Runtime.InteropServices;
using Excalibur.Core.Models;
using Excalibur.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace Excalibur.Core.Devices;

public static class AtaSmartReader
{
    private const byte ATA_SMART = 0xB0;
    private const byte SMART_READ_DATA = 0xD0;
    private const byte SMART_READ_THRESHOLDS = 0xD1;

    // SMART_RCV_DRIVE_DATA structures (from ntdddisk.h)
    private const uint SMART_GET_VERSION = 0x00074080;
    private const uint SMART_RCV_DRIVE_DATA = 0x0007C088;

    [StructLayout(LayoutKind.Sequential)]
    private struct GETVERSIONINPARAMS
    {
        public byte bVersion;
        public byte bRevision;
        public byte bReserved;
        public byte bIDEDeviceMap;
        public uint fCapabilities;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public uint[] dwReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IDEREGS
    {
        public byte bFeaturesReg;
        public byte bSectorCountReg;
        public byte bSectorNumberReg;
        public byte bCylLowReg;
        public byte bCylHighReg;
        public byte bDriveHeadReg;
        public byte bCommandReg;
        public byte bReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SENDCMDINPARAMS
    {
        public uint cBufferSize;
        public IDEREGS irDriveRegs;
        public byte bDriveNumber;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public byte[] bReserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public uint[] dwReserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public byte[] bBuffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DRIVERSTATUS
    {
        public byte bDriverError;
        public byte bIDEError;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public byte[] bReserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public uint[] dwReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SENDCMDOUTPARAMS
    {
        public uint cBufferSize;
        public DRIVERSTATUS DriverStatus;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)]
        public byte[] bBuffer;
    }

    public static AtaSmart Read(SafeFileHandle handle)
    {
        var errors = new List<string>();

        // Attempt 1: IOCTL_ATA_PASS_THROUGH_DIRECT
        var data = SendSmartPassThrough(handle, SMART_READ_DATA, out var ptError);
        if (data != null)
        {
            var thresholds = SendSmartPassThrough(handle, SMART_READ_THRESHOLDS, out _);
            return BuildResult(data, thresholds);
        }
        errors.Add($"ATA_PASS_THROUGH: {ptError}");

        // Attempt 2: SMART_RCV_DRIVE_DATA (legacy)
        data = SendSmartRcvDriveData(handle, SMART_READ_DATA, out var rcvError);
        if (data != null)
        {
            var thresholds = SendSmartRcvDriveData(handle, SMART_READ_THRESHOLDS, out _);
            return BuildResult(data, thresholds);
        }
        errors.Add($"SMART_RCV_DRIVE_DATA: {rcvError}");

        return new AtaSmart
        {
            IsValid = false,
            ReadError = string.Join(" | ", errors),
        };
    }

    private static AtaSmart BuildResult(byte[] data, byte[]? thresholds)
    {
        var attrs = ParseSmartData(data);
        var thrs = thresholds != null ? ParseSmartThresholds(thresholds) : new List<SmartAttribute>();
        var thrMap = thrs.ToDictionary(t => t.Id, t => t.Threshold);

        var merged = new List<SmartAttribute>();
        foreach (var a in attrs)
        {
            thrMap.TryGetValue(a.Id, out var th);
            merged.Add(new SmartAttribute
            {
                Id = a.Id,
                Flags = a.Flags,
                CurrentValue = a.CurrentValue,
                WorstValue = a.WorstValue,
                RawValue = a.RawValue,
                RawValue0 = a.RawValue0,
                Threshold = th,
            });
        }

        return new AtaSmart
        {
            IsValid = true,
            RawHexDump = Convert.ToHexString(data),
            ThresholdHexDump = thresholds != null ? Convert.ToHexString(thresholds) : null,
            RevisionNumber = data.Length > 1 ? data[1] : (byte)0,
            Attributes = merged,
            Thresholds = thrs,
        };
    }

    // ---- Path 1: IOCTL_ATA_PASS_THROUGH_DIRECT ----
    private static byte[]? SendSmartPassThrough(SafeFileHandle handle, byte smartSubCommand, out string error)
    {
        error = "";
        const int dataSize = 512;
        IntPtr dataBuf = NativeMethods.VirtualAlloc(IntPtr.Zero, (UIntPtr)dataSize,
            NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
        if (dataBuf == IntPtr.Zero) { error = "VirtualAlloc failed"; return null; }

        int structSize = Marshal.SizeOf<ATA_PASS_THROUGH_DIRECT>();
        IntPtr aptBuf = Marshal.AllocHGlobal(structSize);
        try
        {
            for (int i = 0; i < dataSize; i++) Marshal.WriteByte(dataBuf, i, 0);

            var apt = new ATA_PASS_THROUGH_DIRECT
            {
                Length = (ushort)structSize,
                AtaFlags = (ushort)(AtaFlags.DRDY | AtaFlags.DataIn),
                DataTransferLength = dataSize,
                TimeOutValue = 10,
                DataBuffer = dataBuf,
                PreviousTaskFile = new byte[8],
                CurrentTaskFile = new byte[8],
            };

            apt.CurrentTaskFile[0] = smartSubCommand; // Features
            apt.CurrentTaskFile[1] = 0;               // SectorCount
            apt.CurrentTaskFile[2] = 0;               // LBA Low
            apt.CurrentTaskFile[3] = 0x4F;            // LBA Mid (SMART sig)
            apt.CurrentTaskFile[4] = 0xC2;            // LBA High (SMART sig)
            apt.CurrentTaskFile[5] = 0xA0;            // DriveHead
            apt.CurrentTaskFile[6] = ATA_SMART;       // Command
            apt.CurrentTaskFile[7] = 0;

            Marshal.StructureToPtr(apt, aptBuf, false);

            bool ok = NativeMethods.DeviceIoControl(handle,
                NativeMethods.IOCTL_ATA_PASS_THROUGH_DIRECT,
                aptBuf, structSize, aptBuf, structSize, out _, IntPtr.Zero);

            if (!ok)
            {
                error = $"DeviceIoControl failed, Win32Error={Marshal.GetLastWin32Error()}";
                return null;
            }

            var result = Marshal.PtrToStructure<ATA_PASS_THROUGH_DIRECT>(aptBuf);
            byte status = result.CurrentTaskFile[6];
            if ((status & 0x01) != 0)
            {
                error = $"ATA ERR bit set, status=0x{status:X2}, errorReg=0x{result.CurrentTaskFile[0]:X2}";
                return null;
            }

            var data = new byte[dataSize];
            Marshal.Copy(dataBuf, data, 0, dataSize);
            return data;
        }
        finally
        {
            Marshal.FreeHGlobal(aptBuf);
            NativeMethods.VirtualFree(dataBuf, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
        }
    }

    // ---- Path 2: SMART_RCV_DRIVE_DATA ----
    private static byte[]? SendSmartRcvDriveData(SafeFileHandle handle, byte smartSubCommand, out string error)
    {
        error = "";

        int gvSize = Marshal.SizeOf<GETVERSIONINPARAMS>();
        IntPtr gvPtr = Marshal.AllocHGlobal(gvSize);
        try
        {
            bool ok = NativeMethods.DeviceIoControl(handle, SMART_GET_VERSION,
                IntPtr.Zero, 0, gvPtr, gvSize, out _, IntPtr.Zero);
            if (!ok)
            {
                error = $"SMART_GET_VERSION failed, Win32Error={Marshal.GetLastWin32Error()}";
                return null;
            }
            var ver = Marshal.PtrToStructure<GETVERSIONINPARAMS>(gvPtr);
            if ((ver.fCapabilities & 0x01) == 0)
            {
                error = $"SMART not supported by device (caps=0x{ver.fCapabilities:X8})";
                return null;
            }
        }
        finally { Marshal.FreeHGlobal(gvPtr); }

        int inSize = Marshal.SizeOf<SENDCMDINPARAMS>();
        int outSize = Marshal.SizeOf<SENDCMDOUTPARAMS>();
        IntPtr inPtr = Marshal.AllocHGlobal(inSize);
        IntPtr outPtr = Marshal.AllocHGlobal(outSize);
        try
        {
            var inParams = new SENDCMDINPARAMS
            {
                cBufferSize = 512,
                irDriveRegs = new IDEREGS
                {
                    bFeaturesReg = smartSubCommand,
                    bSectorCountReg = 1,
                    bSectorNumberReg = 0,
                    bCylLowReg = 0x4F,
                    bCylHighReg = 0xC2,
                    bDriveHeadReg = 0xA0,
                    bCommandReg = ATA_SMART,
                    bReserved = 0,
                },
                bDriveNumber = 0,
                bReserved = new byte[3],
                dwReserved = new uint[4],
                bBuffer = new byte[1],
            };
            Marshal.StructureToPtr(inParams, inPtr, false);

            bool ok = NativeMethods.DeviceIoControl(handle, SMART_RCV_DRIVE_DATA,
                inPtr, inSize, outPtr, outSize, out _, IntPtr.Zero);

            if (!ok)
            {
                error = $"DeviceIoControl failed, Win32Error={Marshal.GetLastWin32Error()}";
                return null;
            }

            var outParams = Marshal.PtrToStructure<SENDCMDOUTPARAMS>(outPtr);
            if (outParams.DriverStatus.bDriverError != 0)
            {
                error = $"DriverStatus error: driverErr={outParams.DriverStatus.bDriverError}, ideErr=0x{outParams.DriverStatus.bIDEError:X2}";
                return null;
            }

            var data = new byte[512];
            Array.Copy(outParams.bBuffer, 0, data, 0, 512);
            return data;
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
            Marshal.FreeHGlobal(outPtr);
        }
    }

    // ---- Parsers ----
    // SMART data structure (ATA8-ACS):
    //   bytes 0..1   : revision number (vendor specific)
    //   bytes 2..3   : offline data collection status
    //   bytes 4..5   : self-test execution status
    //   bytes 6..7   : total time in seconds to complete offline data collection
    //   byte  8      : vendor specific
    //   byte  9      : offline data collection capabilities
    //   bytes 10..11 : SMART capabilities
    //   byte  12     : error logging capability
    //   ...
    //   bytes 2 + i*12 .. 2 + i*12 + 11 : attribute i (i = 0..29)
    //
    // Each attribute:
    //   byte 0   : attribute id (0 = empty slot)
    //   bytes 1..2 : status flags
    //   byte  3  : current value (normalized 1..253)
    //   byte  4  : worst value
    //   bytes 5..10 : raw value (6 bytes, little-endian, vendor-specific packing)
    //   byte  11 : reserved
    private static List<SmartAttribute> ParseSmartData(byte[] raw)
    {
        var list = new List<SmartAttribute>();
        const int minSize = 2 + 30 * 12; // 362
        if (raw.Length < minSize) return list;

        for (int i = 0; i < 30; i++)
        {
            int off = 2 + i * 12;
            byte id = raw[off];
            if (id == 0) continue;

            var attr = new SmartAttribute
            {
                Id = id,
                Flags = (ushort)(raw[off + 1] | (raw[off + 2] << 8)),
                CurrentValue = raw[off + 3],
                WorstValue = raw[off + 4],
                RawValue0 = raw[off + 5],
                RawValue = new byte[6],
            };
            Array.Copy(raw, off + 5, attr.RawValue, 0, 6);
            list.Add(attr);
        }
        return list;
    }

    // SMART thresholds structure (ATA8-ACS):
    //   bytes 0..1 : revision number
    //   bytes 2 + i*12 : threshold for attribute i
    //     byte 0   : attribute id (0 = empty)
    //     byte 1   : threshold value
    //     bytes 2..11 : reserved
    private static List<SmartAttribute> ParseSmartThresholds(byte[] raw)
    {
        var list = new List<SmartAttribute>();
        const int minSize = 2 + 30 * 12;
        if (raw.Length < minSize) return list;

        for (int i = 0; i < 30; i++)
        {
            int off = 2 + i * 12;
            byte id = raw[off];
            if (id == 0) continue;
            list.Add(new SmartAttribute
            {
                Id = id,
                Threshold = raw[off + 1],
            });
        }
        return list;
    }
}