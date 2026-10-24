using System.Runtime.InteropServices;
using Excalibur.Core.Models;
using Excalibur.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace Excalibur.Core.Devices;

public static class NvmeHealthReader
{
    private const int NVME_LOG_PAGE_SMART_HEALTH = 0x02;

    public static NvmeHealth Read(SafeFileHandle handle)
    {
        try
        {
            var raw = ReadLogPage(handle, NVME_LOG_PAGE_SMART_HEALTH, 512);
            if (raw == null)
                return new NvmeHealth { IsValid = false, ReadError = "Get Log Page 0x02 failed" };
            return ParseHealth(raw);
        }
        catch (Exception ex)
        {
            return new NvmeHealth { IsValid = false, ReadError = ex.Message };
        }
    }

    private static byte[]? ReadLogPage(SafeFileHandle handle, uint logPageId, int size)
    {
        IntPtr dataBuf = NativeMethods.VirtualAlloc(IntPtr.Zero, (UIntPtr)size,
            NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
        if (dataBuf == IntPtr.Zero) return null;

        int sptSize = Marshal.SizeOf<STORAGE_PROTOCOL_COMMAND>();
        int nvmeCmdSize = 64;
        int cmdBufSize = sptSize + nvmeCmdSize;
        IntPtr cmdBuf = Marshal.AllocHGlobal(cmdBufSize);

        try
        {
            var spt = new STORAGE_PROTOCOL_COMMAND
            {
                Version = 1,
                Length = (uint)cmdBufSize,
                ProtocolType = 3,
                Flags = 0x1,
                CommandLength = (uint)nvmeCmdSize,
                ErrorInfoLength = 0,
                ErrorInfoOffset = 0,
                DataToDeviceTransferLength = 0,
                DataFromDeviceTransferLength = (uint)size,
                DataToDeviceBufferOffset = (uint)cmdBufSize,
                DataFromDeviceBufferOffset = (uint)cmdBufSize,
                TimeOutValue = 10,
            };

            var nvme = new NVME_COMMAND
            {
                Opcode = 0x02,
                Flags = 0,
                CommandId = 0,
                NamespaceId = 0xFFFFFFFF,
                CDW10 = (uint)(logPageId | (((size / 4) - 1) << 16)),
                CDW11 = 0,
            };

            Marshal.StructureToPtr(spt, cmdBuf, false);
            Marshal.StructureToPtr(nvme, IntPtr.Add(cmdBuf, sptSize), false);

            int total = cmdBufSize + size;
            IntPtr totalBuf = Marshal.AllocHGlobal(total);
            try
            {
                byte[] headerBytes = new byte[cmdBufSize];
                Marshal.Copy(cmdBuf, headerBytes, 0, cmdBufSize);
                Marshal.Copy(headerBytes, 0, totalBuf, cmdBufSize);
                for (int i = 0; i < size; i++) Marshal.WriteByte(totalBuf, cmdBufSize + i, 0);

                var sptRead = Marshal.PtrToStructure<STORAGE_PROTOCOL_COMMAND>(totalBuf);
                sptRead.DataFromDeviceBufferOffset = (uint)cmdBufSize;
                sptRead.DataFromDeviceTransferLength = (uint)size;
                Marshal.StructureToPtr(sptRead, totalBuf, false);

                bool ok = NativeMethods.DeviceIoControl(handle,
                    NativeMethods.IOCTL_STORAGE_PROTOCOL_COMMAND,
                    totalBuf, total, totalBuf, total, out _, IntPtr.Zero);
                if (!ok) return null;

                var rslt = Marshal.PtrToStructure<STORAGE_PROTOCOL_COMMAND>(totalBuf);
                if (rslt.ReturnStatus != 0) return null;

                var data = new byte[size];
                Marshal.Copy(IntPtr.Add(totalBuf, cmdBufSize), data, 0, size);
                return data;
            }
            finally { Marshal.FreeHGlobal(totalBuf); }
        }
        finally
        {
            Marshal.FreeHGlobal(cmdBuf);
            NativeMethods.VirtualFree(dataBuf, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
        }
    }

    private static NvmeHealth ParseHealth(byte[] raw)
    {
        return new NvmeHealth
        {
            IsValid = true,
            RawHexDump = Convert.ToHexString(raw, 0, Math.Min(256, raw.Length)),
            CriticalWarning = raw[0],
            CompositeTemperatureKelvin = (ushort)((uint)raw[1] | ((uint)raw[2] << 8)),
            AvailableSpare = raw[3],
            AvailableSpareThreshold = raw[4],
            PercentageUsed = raw[5],
            DataUnitsRead = ReadUInt128(raw, 32),
            DataUnitsWritten = ReadUInt128(raw, 48),
            HostReadCommands = ReadUInt128(raw, 64),
            HostWriteCommands = ReadUInt128(raw, 80),
            ControllerBusyTime = ReadUInt128(raw, 96),
            PowerCycles = ReadUInt128(raw, 112),
            PowerOnHours = ReadUInt128(raw, 128),
            UnsafeShutdowns = ReadUInt128(raw, 144),
            MediaErrors = ReadUInt128(raw, 160),
            ErrorLogEntries = ReadUInt128(raw, 176),
        };
    }

    private static ulong ReadUInt128(byte[] raw, int offset)
    {
        ulong lo = 0;
        for (int i = 0; i < 8; i++) lo |= (ulong)raw[offset + i] << (8 * i);
        return lo;
    }
}