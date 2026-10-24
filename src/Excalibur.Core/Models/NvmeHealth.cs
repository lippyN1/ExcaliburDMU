namespace Excalibur.Core.Models;

public sealed class NvmeHealth
{
    public bool IsValid { get; init; }
    public string? ReadError { get; init; }
    public string? RawHexDump { get; init; }
    public byte CriticalWarning { get; init; }
    public ushort CompositeTemperatureKelvin { get; init; }
    public byte AvailableSpare { get; init; }
    public byte AvailableSpareThreshold { get; init; }
    public byte PercentageUsed { get; init; }
    public ulong DataUnitsRead { get; init; }
    public ulong DataUnitsWritten { get; init; }
    public ulong HostReadCommands { get; init; }
    public ulong HostWriteCommands { get; init; }
    public ulong ControllerBusyTime { get; init; }
    public ulong PowerCycles { get; init; }
    public ulong PowerOnHours { get; init; }
    public ulong UnsafeShutdowns { get; init; }
    public ulong MediaErrors { get; init; }
    public ulong ErrorLogEntries { get; init; }
}