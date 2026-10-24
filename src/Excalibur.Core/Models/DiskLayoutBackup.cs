namespace Excalibur.Core.Models;

public sealed class DiskLayoutBackup
{
    public int DiskNumber { get; init; }
    public long SectorSize { get; init; }
    public long TotalSectors { get; init; }

    public byte[] Mbr { get; init; } = Array.Empty<byte>();
    public byte[]? GptPrimaryHeader { get; init; }
    public byte[]? GptPartitionEntries { get; init; }
    public byte[]? GptBackupHeader { get; init; }
    public byte[]? GptBackupEntries { get; init; }
    public Dictionary<long, byte[]> VbrSectors { get; init; } = new();

    public bool HasGpt => GptPrimaryHeader != null;
    public bool HasMbr => Mbr.Length > 0;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
