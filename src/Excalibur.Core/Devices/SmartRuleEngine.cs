using System.Text.Json;
using System.Text.RegularExpressions;
using Excalibur.Core.Models;

namespace Excalibur.Core.Devices;

public sealed class SmartRuleEngine
{
    private readonly AtaAttributesTable _ataAttrs;
    private readonly VendorOverridesTable _vendor;
    private readonly VerdictTable _verdict;
    public string RulesVersion { get; }

    private SmartRuleEngine(AtaAttributesTable ata, VendorOverridesTable vendor, VerdictTable verdict, string version)
    {
        _ataAttrs = ata;
        _vendor = vendor;
        _verdict = verdict;
        RulesVersion = version;
    }

    public static SmartRuleEngine Load(string rulesDir)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var ata = JsonSerializer.Deserialize<AtaAttributesTable>(
            File.ReadAllText(Path.Combine(rulesDir, "ata-attributes.json")), opts)
            ?? throw new InvalidOperationException("ata-attributes.json empty");
        var vendor = JsonSerializer.Deserialize<VendorOverridesTable>(
            File.ReadAllText(Path.Combine(rulesDir, "vendor-overrides.json")), opts)
            ?? throw new InvalidOperationException("vendor-overrides.json empty");
        var verdict = JsonSerializer.Deserialize<VerdictTable>(
            File.ReadAllText(Path.Combine(rulesDir, "verdict.json")), opts)
            ?? throw new InvalidOperationException("verdict.json empty");
        var version = File.ReadAllText(Path.Combine(rulesDir, "rules-version.txt")).Trim();

        return new SmartRuleEngine(ata, vendor, verdict, version);
    }

    public void Interpret(AtaSmart smart, PhysicalDisk disk)
    {
        if (smart.Attributes.Count == 0)
        {
            SetVerdict(smart, "Unknown", new List<string> { "SMART attributes empty" });
            return;
        }

        var vendorOverrides = ResolveVendor(disk);

        foreach (var attr in smart.Attributes)
        {
            var meta = LookupMeta(attr.Id);
            SetName(attr, meta?.Name ?? "Attribute_" + attr.Id.ToString("X2"));

            VendorAttrOverride? ov = null;
            if (vendorOverrides != null)
                vendorOverrides.Attributes.TryGetValue(attr.Id.ToString(), out ov);

            var (display, health) = Decode(attr, meta, ov);
            SetDisplay(attr, display);
            SetHealth(attr, health);
        }

        var reasons = new List<string>();
        string verdict = "Healthy";

        var failingRules = _verdict.Verdicts.TryGetValue("failing", out var f) ? f.Rules : new List<VerdictRule>();
        var warningRules = _verdict.Verdicts.TryGetValue("warning", out var w) ? w.Rules : new List<VerdictRule>();

        if (EvaluateRules(failingRules, smart, reasons))
        {
            verdict = "Failing";
        }
        else if (EvaluateRules(warningRules, smart, reasons))
        {
            verdict = "Warning";
        }
        else
        {
            reasons.Add("All monitored attributes are within expected values");
        }

        SetVerdict(smart, verdict, reasons);
    }

    private AttributeMeta? LookupMeta(byte id) =>
        _ataAttrs.Attributes.TryGetValue(id.ToString(), out var m) ? m : null;

    private VendorOverride? ResolveVendor(PhysicalDisk disk)
    {
        var model = disk.Model ?? "";
        var firmware = disk.Firmware ?? "";
        var serial = disk.SerialNumber ?? "";

        foreach (var ov in _vendor.Overrides)
        {
            var m = ov.Match;
            if (m.ModelRegex != null && !Regex.IsMatch(model, m.ModelRegex, RegexOptions.IgnoreCase)) continue;
            if (m.FirmwareRegex != null && !Regex.IsMatch(firmware, m.FirmwareRegex, RegexOptions.IgnoreCase)) continue;
            if (m.SerialRegex != null && !Regex.IsMatch(serial, m.SerialRegex, RegexOptions.IgnoreCase)) continue;
            return ov;
        }
        return null;
    }

    private (string display, string health) Decode(SmartAttribute attr, AttributeMeta? meta, VendorAttrOverride? ov)
    {
        ulong r = attr.RawValueNumeric ?? 0;

        if (ov != null)
        {
            var overrideResult = ApplyDecoder(ov.Decode, attr);
            if (overrideResult != null)
                return (overrideResult, "Info");
        }

        if (meta == null)
            return (r.ToString("N0"), "Info");

        // Vendor-packed attributes without an explicit override: we don't know
        // how to decode them, so say so honestly instead of showing garbage.
        if (meta.VendorPacked)
            return ("<packed, vendor-specific>", "Info");

        if (meta.IsTemperature)
        {
            int t = attr.RawValue[0];
            return (t.ToString() + " °C", t > 55 ? "Warning" : "Ok");
        }

        if (meta.HealthFromValue)
        {
            int pct = attr.CurrentValue;
            return (pct.ToString() + " %", pct < 10 ? "Critical" : pct < 30 ? "Warning" : "Ok");
        }

        if (meta.PreFail && r > 0)
            return (r.ToString("N0") + " " + meta.Unit, "Critical");

        if (meta.CriticalNonZero && r > 0)
            return (r.ToString("N0") + " " + meta.Unit, "Warning");

        if (meta.CableWarning)
            return r > 0
                ? (r.ToString("N0") + " " + meta.Unit + " (check cable)", "Warning")
                : (r.ToString("N0") + " " + meta.Unit, "Ok");

        var s = (r.ToString("N0") + " " + meta.Unit).Trim();
        return (s, "Ok");
    }

    private static string? ApplyDecoder(string decoder, SmartAttribute attr)
    {
        var b = attr.RawValue;
        if (b.Length < 6) return null;

        ulong v;
        switch (decoder)
        {
            case "raw_no_decode":
                return null;

            case "be16_at_0_1":
                v = ((ulong)b[0] << 8) | b[1];
                return v.ToString("N0") + " h";

            case "be16_at_2_3":
                v = ((ulong)b[2] << 8) | b[3];
                return v.ToString("N0") + " h";

            case "le16_at_0_1":
                v = (ulong)b[0] | ((ulong)b[1] << 8);
                return v.ToString("N0") + " h";

            case "le32_at_0_3":
                v = (ulong)b[0]
                  | ((ulong)b[1] << 8)
                  | ((ulong)b[2] << 16)
                  | ((ulong)b[3] << 24);
                return v.ToString("N0") + " h";

            case "le48_at_0_5_lba":
                // 48-bit LBA counter -> human-readable size (assume 512-byte sectors)
                v = (ulong)b[0]
                  | ((ulong)b[1] << 8)
                  | ((ulong)b[2] << 16)
                  | ((ulong)b[3] << 24)
                  | ((ulong)b[4] << 32)
                  | ((ulong)b[5] << 40);
                double bytes = (double)v * 512.0;
                return FormatBytes(bytes);

            case "first_byte":
                return b[0].ToString();

            case "current_value_pct":
                return attr.CurrentValue.ToString() + " %";

            default:
                return null;
        }
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        int i = 0;
        while (bytes >= 1024 && i < units.Length - 1)
        {
            bytes /= 1024;
            i++;
        }
        return bytes.ToString("N2") + " " + units[i];
    }
    private static bool EvaluateRules(List<VerdictRule> rules, AtaSmart smart, List<string> reasons)
    {
        bool any = false;
        foreach (var rule in rules)
        {
            var attr = smart.Attributes.FirstOrDefault(a => a.Id == rule.AttrId);
            if (attr == null) continue;

            ulong r = attr.RawValueNumeric ?? 0;
            bool hit = rule.Type switch
            {
                "attr_above" => r > (ulong)rule.Value,
                "attr_below_percent" => attr.CurrentValue < rule.Value,
                "attr_above_temp" => attr.RawValue.Length > 0 && attr.RawValue[0] > rule.Value,
                _ => false,
            };

            if (hit)
            {
                reasons.Add("[" + attr.Id + "] " + rule.Reason + ": " + (attr.Display ?? r.ToString()));
                any = true;
            }
        }
        return any;
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