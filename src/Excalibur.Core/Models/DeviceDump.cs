namespace Excalibur.Core.Models;

public sealed class DeviceDump
{
    public string Tool { get; init; } = "Excalibur";
    public string ToolVersion { get; init; } = "1.0.0-alpha";
    public string RulesVersion { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public bool Elevated { get; init; }
    public List<PhysicalDisk> Disks { get; init; } = new();
}