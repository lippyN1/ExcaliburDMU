namespace Excalibur.Core.Models;

public sealed class PhysicalDisk
{
    public int DiskNumber { get; init; }
    public string Path { get; init; } = "";
    public string? Model { get; init; }
    public string? SerialNumber { get; init; }
    public string? Firmware { get; init; }
    public string? VendorId { get; init; }
    public string? ProductId { get; init; }
    public byte DeviceType { get; init; }
    public bool RemovableMedia { get; init; }
    public bool CommandQueueing { get; init; }
    public string BusTypeName { get; init; } = "Unknown";
    public int BusTypeCode { get; init; }
    public long? CapacityBytes { get; init; }
    public uint? LogicalSectorSize { get; init; }
    public uint? PhysicalSectorSize { get; init; }
    public List<VolumeInfo> Volumes { get; init; } = new();
    public AtaSmart? AtaSmart { get; set; }
    public NvmeHealth? NvmeHealth { get; set; }
    public string? SmartAccessNote { get; set; }
}

public sealed class VolumeInfo
{
    public string MountPoint { get; init; } = "";
    public long SizeBytes { get; init; }
    public long FreeBytes { get; init; }
}