namespace Excalibur.Core.Models;

public sealed class BadBlock
{
    public long LbaStart { get; init; }
    public long LbaEnd { get; init; }
    public int SectorCount { get; init; }
    public int Win32Error { get; init; }
    public string ErrorName { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }
}
