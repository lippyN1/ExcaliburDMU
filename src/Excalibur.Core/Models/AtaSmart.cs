namespace Excalibur.Core.Models;

public sealed class AtaSmart
{
    public bool IsValid { get; init; }
    public string? RawHexDump { get; init; }
    public string? ThresholdHexDump { get; init; }
    public byte RevisionNumber { get; init; }
    public byte[]? OfflineDataCollectionStatus { get; init; }
    public ushort SmartCapabilities { get; init; }
    public List<SmartAttribute> Attributes { get; init; } = new();
    public List<SmartAttribute> Thresholds { get; init; } = new();
    public string? ReadError { get; init; }

    public string Verdict { get; init; } = "Unknown";
    public List<string> VerdictReasons { get; init; } = new();
}

public sealed class SmartAttribute
{
    public byte Id { get; init; }
    public ushort Flags { get; init; }
    public byte CurrentValue { get; init; }
    public byte WorstValue { get; init; }
    public byte RawValue0 { get; init; }
    public byte[] RawValue { get; init; } = new byte[6];
    public byte Threshold { get; init; }

    // Added by interpreter
    public string? Name { get; init; }
    public string? Display { get; init; }
    public string? Health { get; init; }  // "Ok" | "Warning" | "Critical" | "Info"

    public ulong? RawValueNumeric => TryParseRaw();

    private ulong? TryParseRaw()
    {
        if (RawValue.Length < 6) return null;
        ulong v = 0;
        for (int i = 0; i < 6; i++) v |= (ulong)RawValue[i] << (8 * i);
        return v;
    }
}