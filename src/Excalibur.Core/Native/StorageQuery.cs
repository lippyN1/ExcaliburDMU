using System.Runtime.InteropServices;

namespace Excalibur.Core.Native;

[StructLayout(LayoutKind.Sequential)]
public struct STORAGE_PROPERTY_QUERY
{
    public int PropertyId;
    public int QueryType;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
    public byte[] AdditionalParameters;
}

[StructLayout(LayoutKind.Sequential)]
public struct STORAGE_DEVICE_DESCRIPTOR
{
    public uint Version;
    public uint Size;
    public byte DeviceType;
    public byte DeviceTypeModifier;
    public byte RemovableMedia;
    public byte CommandQueueing;
    public uint VendorIdOffset;
    public uint ProductIdOffset;
    public uint ProductRevisionOffset;
    public uint SerialNumberOffset;
    public int BusType;
    public uint RawPropertiesLength;
}

[StructLayout(LayoutKind.Sequential)]
public struct STORAGE_DEVICE_NUMBER
{
    public uint DeviceType;
    public uint DeviceNumber;
    public uint PartitionNumber;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISK_EXTENT
{
    public uint DiskNumber;
    public long StartingOffset;
    public long ExtentLength;
}

[StructLayout(LayoutKind.Sequential)]
public struct VOLUME_DISK_EXTENTS
{
    public uint NumberOfDiskExtents;
}

public enum StorageBusType
{
    Unknown = 0, Scsi = 1, Atapi = 2, Ata = 3, Ieee1394 = 4, Ssa = 5,
    Fibre = 6, Usb = 7, Raid = 8, IScsi = 9, Sas = 10, Sata = 11,
    Sd = 12, Mmc = 13, Virtual = 14, FileBackedVirtual = 15, Spaces = 16,
    Nvme = 17, SCM = 18, Ufs = 19, Max = 20
}