namespace Excalibur.Core.Models;

public sealed class BucketStats
{
    public int Index { get; set; }
    public long LbaStart { get; set; }
    public long LbaEnd { get; set; }
    public long BytesStart { get; set; }
    public long BytesEnd { get; set; }
    public double MinSpeedMbps { get; set; }
    public double MaxSpeedMbps { get; set; }
    public double SumSpeedMbps { get; set; }
    public long BlocksRead { get; set; }
    public long BytesRead { get; set; }
    public double ElapsedSeconds { get; set; }
    public int BadSectors { get; set; }

    public double AvgSpeedMbps => ElapsedSeconds > 0 ? (BytesRead / 1_048_576.0) / ElapsedSeconds : 0;
    public bool HasData => BytesRead > 0 || BadSectors > 0;
}
