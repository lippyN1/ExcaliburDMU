using System.Runtime.InteropServices;

namespace Excalibur.Core.Native;

[StructLayout(LayoutKind.Sequential)]
public struct ATA_PASS_THROUGH_DIRECT
{
    public ushort Length;
    public ushort AtaFlags;
    public byte PathId;
    public byte TargetId;
    public byte Lun;
    public byte ReservedAsUchar;
    public uint DataTransferLength;
    public uint TimeOutValue;
    public uint ReservedAsUlong;
    public IntPtr DataBuffer;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public byte[] PreviousTaskFile;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public byte[] CurrentTaskFile;
}

// ATA_FLAGS_* from ntdddisk.h
[Flags]
public enum AtaFlags : ushort
{
    None = 0,
    DRDY = 0x01,          // ATA_FLAGS_DRDY_REQUIRED
    DataIn = 0x02,        // ATA_FLAGS_DATA_IN    (device -> host)
    DataOut = 0x04,       // ATA_FLAGS_DATA_OUT   (host -> device)
    Command48Bit = 0x08,  // ATA_FLAGS_48BIT_COMMAND
    UseDma = 0x10,        // ATA_FLAGS_USE_DMA
    NoMultiple = 0x20,    // ATA_FLAGS_NO_MULTIPLE
}