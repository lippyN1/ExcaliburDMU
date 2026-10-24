using System.Text.Json.Serialization;

namespace Excalibur.Core.Models;

public sealed class TestState
{
    public int SchemaVersion { get; set; } = 1;

    public int DiskNumber { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public uint? LogicalSectorSize { get; set; }
    public long DiskCapacityBytes { get; set; }

    public long StartLba { get; set; }
    public long EndLba { get; set; }
    public int BlockSizeBytes { get; set; }
    public int BucketCount { get; set; }

    public long LastCompletedLba { get; set; }
    public DateTimeOffset LastSavedAt { get; set; }

    public double ElapsedSecondsBeforeResume { get; set; }
    public long BytesReadBeforeResume { get; set; }
    public long BlocksReadBeforeResume { get; set; }

    public List<BadBlock> BadBlocks { get; set; } = new();
    public List<BucketStats> Buckets { get; set; } = new();

    [JsonIgnore]
    public bool IsFinished => LastCompletedLba >= EndLba;
}