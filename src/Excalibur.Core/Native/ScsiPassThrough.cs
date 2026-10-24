using System.Runtime.InteropServices;

namespace Excalibur.Core.Native;

[StructLayout(LayoutKind.Sequential)]
public struct SCSI_PASS_THROUGH_DIRECT
{
    public ushort Length;
    public byte ScsiStatus;
    public byte PathId;
    public byte TargetId;
    public byte Lun;
    public byte CdbLength;
    public byte SenseInfoLength;
    public byte DataIn;
    public uint DataTransferLength;
    public uint TimeOutValue;
    public IntPtr DataBuffer;
    public uint SenseInfoOffset;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public byte[] Cdb;
}