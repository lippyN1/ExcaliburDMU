namespace Excalibur.Core.Models;

public sealed class CapacityTestResult
{
    public int DiskNumber { get; init; }
    public string? Model { get; init; }
    public string? SerialNumber { get; init; }
    public long DeclaredCapacityBytes { get; init; }
    public long ActualCapacityBytes { get; init; }
    public uint? LogicalSectorSize { get; init; }

    public bool FullScan { get; init; }
    public long MarkerSizeBytes { get; init; }
    public int MarkerCount { get; init; }

    public double WriteSpeedMbps { get; init; }
    public double ReadSpeedMbps { get; init; }

    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public double ElapsedSeconds { get; init; }

    public List<CapacityMarker> Markers { get; init; } = new();
    public List<string> Warnings { get; init; } = new();

    public string Verdict { get; init; } = "Unknown";
    public List<string> VerdictReasons { get; init; } = new();

    public string Status { get; init; } = "ok";
    public string? Error { get; init; }
}

public sealed class CapacityMarker
{
    public int Index { get; init; }
    public long Lba { get; init; }
    public long ByteOffset { get; init; }
    public int SizeBytes { get; init; }
    public string Hash { get; init; } = "";
    public bool WriteOk { get; init; }
    public bool ReadOk { get; init; }
    public bool HashMatches { get; init; }
    public string? Error { get; init; }
}
