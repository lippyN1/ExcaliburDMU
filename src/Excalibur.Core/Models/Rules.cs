using System.Text.Json.Serialization;

namespace Excalibur.Core.Models;

public sealed class AtaAttributesTable
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("attributes")] public Dictionary<string, AttributeMeta> Attributes { get; set; } = new();
}

public sealed class AttributeMeta
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("unit")] public string Unit { get; set; } = "";
    [JsonPropertyName("vendor_packed")] public bool VendorPacked { get; set; }
    [JsonPropertyName("critical_nonzero")] public bool CriticalNonZero { get; set; }
    [JsonPropertyName("prefail")] public bool PreFail { get; set; }
    [JsonPropertyName("is_temperature")] public bool IsTemperature { get; set; }
    [JsonPropertyName("cable_warning")] public bool CableWarning { get; set; }
    [JsonPropertyName("health_from_value")] public bool HealthFromValue { get; set; }
}

public sealed class VendorOverridesTable
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("overrides")] public List<VendorOverride> Overrides { get; set; } = new();
}

public sealed class VendorOverride
{
    [JsonPropertyName("vendor")] public string Vendor { get; set; } = "";
    [JsonPropertyName("match")] public VendorMatch Match { get; set; } = new();
    [JsonPropertyName("attributes")] public Dictionary<string, VendorAttrOverride> Attributes { get; set; } = new();
}

public sealed class VendorMatch
{
    [JsonPropertyName("model_regex")] public string? ModelRegex { get; set; }
    [JsonPropertyName("firmware_regex")] public string? FirmwareRegex { get; set; }
    [JsonPropertyName("serial_regex")] public string? SerialRegex { get; set; }
}

public sealed class VendorAttrOverride
{
    [JsonPropertyName("decode")] public string Decode { get; set; } = "raw_no_decode";
}

public sealed class VerdictTable
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("verdicts")] public Dictionary<string, VerdictDefinition> Verdicts { get; set; } = new();
}

public sealed class VerdictDefinition
{
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("rules")] public List<VerdictRule> Rules { get; set; } = new();
}

public sealed class VerdictRule
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("attr_id")] public int AttrId { get; set; }
    [JsonPropertyName("value")] public long Value { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}