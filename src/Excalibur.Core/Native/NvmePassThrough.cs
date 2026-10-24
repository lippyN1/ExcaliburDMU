using System.Runtime.InteropServices;

namespace Excalibur.Core.Native;

[StructLayout(LayoutKind.Sequential)]
public struct STORAGE_PROPERTY_QUERY_EX
{
    public int PropertyId;
    public int QueryType;
    public int ProtocolType;
    public int ProtocolDataOffset;
    public int ProtocolDataLength;
}

[StructLayout(LayoutKind.Sequential)]
public struct STORAGE_PROTOCOL_COMMAND
{
    public uint Version;
    public uint Length;
    public uint ProtocolType;
    public uint Flags;
    public uint ReturnStatus;
    public uint ErrorCode;
    public uint CommandLength;
    public uint ErrorInfoLength;
    public uint DataToDeviceTransferLength;
    public uint DataFromDeviceTransferLength;
    public uint TimeOutValue;
    public uint ErrorInfoOffset;
    public uint DataToDeviceBufferOffset;
    public uint DataFromDeviceBufferOffset;
    public uint CommandSpecific;
    public uint Reserved0;
    public uint FixedProtocolReturnData;
    public uint Reserved1;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public uint[] Reserved2;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NVME_COMMAND
{
    public byte Opcode;
    public byte Flags;
    public ushort CommandId;
    public uint NamespaceId;
    public uint CDW2;
    public uint CDW3;
    public ulong Metadata;
    public ulong PRP1;
    public ulong PRP2;
    public uint CDW10;
    public uint CDW11;
    public uint CDW12;
    public uint CDW13;
    public uint CDW14;
    public uint CDW15;
}