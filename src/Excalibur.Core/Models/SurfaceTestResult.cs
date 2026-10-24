namespace Excalibur.Core.Models;

public sealed class SurfaceTestResult
{
    public int DiskNumber { get; init; }
    public string? Model { get; init; }
    public string? SerialNumber { get; init; }
    public uint? LogicalSectorSize { get; init; }

    public long StartLba { get; init; }
    public long EndLba { get; init; }
    public long TotalBytes { get; init; }
    public int BlockSizeBytes { get; init; }

    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public double ElapsedSeconds { get; init; }

    public long BlocksTotal { get; init; }
    public long BlocksRead { get; init; }
    public long BytesRead { get; init; }

    public double AverageSpeedMbps { get; init; }
    public double MinSpeedMbps { get; init; }
    public double MaxSpeedMbps { get; init; }
    public double MedianSpeedMbps { get; init; }

    public long WorstLba { get; init; }
    public double WorstSpeedMbps { get; init; }

    public List<BadBlock> BadBlocks { get; init; } = new();
    public int TotalBadSectors { get; init; }

    public int SlowBlockCount { get; init; }
    public int SlowBucketCount { get; init; }

    public CalibrationInfo? Calibration { get; init; }
    public SurfaceMap? Map { get; init; }

    public string? HtmlReportPath { get; init; }

    public string Status { get; init; } = "ok";
    public string? Error { get; init; }
}

public sealed class CalibrationInfo
{
    public bool Performed { get; init; }
    public int ChosenBlockSizeBytes { get; init; }
    public double ChosenSpeedMbps { get; init; }
    public List<CalibrationSample> Samples { get; init; } = new();
}

public sealed class CalibrationSample
{
    public int BlockSizeBytes { get; init; }
    public double SpeedMbps { get; init; }
    public double ElapsedSeconds { get; init; }
    public bool Success { get; init; }
}

public sealed class SurfaceMap
{
    public long BucketSizeBytes { get; init; }
    public long BucketSizeMb { get; init; }
    public int BucketCount { get; init; }
    public List<BucketStats> Buckets { get; init; } = new();
}
