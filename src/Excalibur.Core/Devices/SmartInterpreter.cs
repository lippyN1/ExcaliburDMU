using Excalibur.Core.Models;

namespace Excalibur.Core.Devices;

/// <summary>
/// Interprets raw SMART attributes into human-readable form and computes a verdict.
/// v0.1: hardcoded table. Later will be external YAML rule table (per TZ 4.7).
/// </summary>
public static class SmartInterpreter
{
    private sealed class AttrMeta
    {
        public required string Name { get; init; }
        public required string Unit { get; init; }
        public bool IsCritical { get; init; }
        public bool IsPreFail { get; init; }
        public bool IsVendorPack { get; init; }
        public bool IsTemperature { get; init; }
    }

    private static readonly Dictionary<byte, AttrMeta> Ata = new()
    {
        [1]   = new() { Name = "Read_Error_Rate",           Unit = "errors", IsVendorPack = true },
        [3]   = new() { Name = "Spin_Up_Time",              Unit = "ms",     IsVendorPack = true },
        [4]   = new() { Name = "Start_Stop_Count",          Unit = "cycles" },
        [5]   = new() { Name = "Reallocated_Sector_Ct",     Unit = "sectors", IsCritical = true, IsPreFail = true },
        [7]   = new() { Name = "Seek_Error_Rate",           Unit = "errors", IsVendorPack = true },
        [9]   = new() { Name = "Power_On_Hours",            Unit = "h" },
        [10]  = new() { Name = "Spin_Retry_Count",          Unit = "retries", IsVendorPack = true },
        [12]  = new() { Name = "Power_Cycle_Count",         Unit = "cycles" },
        [168] = new() { Name = "SATA_PHY_Error_Count",      Unit = "errors" },
        [170] = new() { Name = "Bad_Block_Count",           Unit = "blocks" },
        [173] = new() { Name = "Wear_Leveling_Count",       Unit = "cycles", IsVendorPack = true },
        [183] = new() { Name = "Runtime_Bad_Block",         Unit = "blocks", IsCritical = true },
        [184] = new() { Name = "End-to-End_Error",          Unit = "errors", IsCritical = true, IsVendorPack = true },
        [187] = new() { Name = "Reported_Uncorrect",        Unit = "errors", IsCritical = true },
        [188] = new() { Name = "Command_Timeout",           Unit = "timeouts", IsCritical = true, IsVendorPack = true },
        [189] = new() { Name = "High_Fly_Writes",           Unit = "writes", IsVendorPack = true },
        [190] = new() { Name = "Airflow_Temperature",       Unit = "°C",     IsTemperature = true },
        [191] = new() { Name = "G-Sense_Error_Rate",        Unit = "events" },
        [192] = new() { Name = "Power-Off_Retract_Count",   Unit = "cycles" },
        [193] = new() { Name = "Load_Cycle_Count",          Unit = "cycles" },
        [194] = new() { Name = "Temperature",               Unit = "°C",     IsTemperature = true },
        [195] = new() { Name = "Hardware_ECC_Recovered",    Unit = "errors", IsVendorPack = true },
        [196] = new() { Name = "Reallocated_Event_Count",   Unit = "events", IsCritical = true },
        [197] = new() { Name = "Current_Pending_Sector",    Unit = "sectors", IsCritical = true, IsPreFail = true },
        [198] = new() { Name = "Offline_Uncorrectable",     Unit = "sectors", IsCritical = true, IsPreFail = true },
        [199] = new() { Name = "UDMA_CRC_Error_Count",      Unit = "errors" },
        [218] = new() { Name = "CRC_Error_Count",           Unit = "errors" },
        [231] = new() { Name = "SSD_Life_Left",             Unit = "%",      IsVendorPack = true },
        [233] = new() { Name = "Media_Wearout_Indicator",   Unit = "%",      IsVendorPack = true },
        [240] = new() { Name = "Head_Flying_Hours",         Unit = "h",      IsVendorPack = true },
        [241] = new() { Name = "Total_LBAs_Written",        Unit = "LBAs",   IsVendorPack = true },
        [242] = new() { Name = "Total_LBAs_Read",           Unit = "LBAs",   IsVendorPack = true },
    };

    public static void Interpret(AtaSmart smart, PhysicalDisk disk)
    {
        if (smart.Attributes.Count == 0)
        {
            SetVerdict(smart, "Unknown", new List<string> { "SMART attributes empty" });
            return;
        }

        bool isSeagate = (disk.Model ?? "").Contains("ST", StringComparison.OrdinalIgnoreCase);
        var reasons = new List<string>();
        int criticalCount = 0;
        int warningCount = 0;

        foreach (var attr in smart.Attributes)
        {
            if (!Ata.TryGetValue(attr.Id, out var meta))
            {
                SetName(attr, $"Attribute_{attr.Id:X2}");
                SetDisplay(attr, attr.RawValueNumeric?.ToString() ?? "?");
                SetHealth(attr, "Info");
                continue;
            }

            SetName(attr, meta.Name);
            var (rawDisplay, health) = Decode(attr, meta, isSeagate);
            SetDisplay(attr, rawDisplay);
            SetHealth(attr, health);

            if (health == "Critical") criticalCount++;
            else if (health == "Warning") warningCount++;
        }

        string verdict;
        if (criticalCount > 0)
        {
            verdict = "Failing";
            reasons.Add($"{criticalCount} critical attribute(s)");
        }
        else if (warningCount > 0)
        {
            verdict = "Warning";
            reasons.Add($"{warningCount} warning attribute(s)");
        }
        else
        {
            verdict = "Healthy";
            reasons.Add("All monitored attributes are within expected values");
        }

        var realloc = smart.Attributes.FirstOrDefault(a => a.Id == 5);
        if (realloc?.RawValueNumeric is > 0)
            reasons.Add($"Reallocated sectors: {realloc.RawValueNumeric}");
        var pending = smart.Attributes.FirstOrDefault(a => a.Id == 197);
        if (pending?.RawValueNumeric is > 0)
            reasons.Add($"Pending sectors: {pending.RawValueNumeric}");
        var crc = smart.Attributes.FirstOrDefault(a => a.Id is 199 or 218);
        if (crc?.RawValueNumeric is > 0)
            reasons.Add($"CRC errors: {crc.RawValueNumeric} (check cable/port)");

        SetVerdict(smart, verdict, reasons);
    }

    private static (string display, string health) Decode(SmartAttribute attr, AttrMeta meta, bool isSeagate)
    {
        ulong? raw = attr.RawValueNumeric;
        if (raw == null) return ("?", "Info");
        ulong r = raw.Value;

        if (meta.IsVendorPack)
        {
            if (meta.Name == "Power_On_Hours" && isSeagate)
            {
                ulong h = (ulong)attr.RawValue[0]
                        | ((ulong)attr.RawValue[1] << 8)
                        | ((ulong)attr.RawValue[2] << 16)
                        | ((ulong)attr.RawValue[3] << 24);
                return ($"{h} h", "Info");
            }
            if (meta.Name == "SSD_Life_Left" || meta.Name == "Media_Wearout_Indicator")
            {
                int pct = attr.CurrentValue;
                return ($"{pct} %", pct < 10 ? "Critical" : pct < 30 ? "Warning" : "Ok");
            }
            return ($"{r:N0}", "Info");
        }

        if (meta.IsTemperature)
        {
            int t = attr.RawValue[0];
            return ($"{t} °C", t > 55 ? "Warning" : "Ok");
        }

        if (meta.IsPreFail && r > 0)
            return ($"{r:N0} {meta.Unit}", "Critical");

        if (meta.IsCritical && r > 0)
            return ($"{r:N0} {meta.Unit}", "Warning");

        if (meta.Name == "UDMA_CRC_Error_Count" || meta.Name == "CRC_Error_Count")
            return r > 0 ? ($"{r:N0} {meta.Unit} (check cable)", "Warning") : ($"{r:N0} {meta.Unit}", "Ok");

        return ($"{r:N0} {meta.Unit}".Trim(), "Ok");
    }

    private static void SetName(SmartAttribute a, string name) =>
        typeof(SmartAttribute).GetProperty(nameof(SmartAttribute.Name))!.SetValue(a, name);
    private static void SetDisplay(SmartAttribute a, string d) =>
        typeof(SmartAttribute).GetProperty(nameof(SmartAttribute.Display))!.SetValue(a, d);
    private static void SetHealth(SmartAttribute a, string h) =>
        typeof(SmartAttribute).GetProperty(nameof(SmartAttribute.Health))!.SetValue(a, h);
    private static void SetVerdict(AtaSmart s, string v, List<string> r)
    {
        typeof(AtaSmart).GetProperty(nameof(AtaSmart.Verdict))!.SetValue(s, v);
        typeof(AtaSmart).GetProperty(nameof(AtaSmart.VerdictReasons))!.SetValue(s, r);
    }
}