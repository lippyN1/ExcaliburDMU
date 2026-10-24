using System.Text.Json;
using System.Text.Json.Serialization;
using Excalibur.Core.Devices;
using Excalibur.Core.Models;
using Excalibur.Core.Native;

namespace Excalibur.Core.Dump;

public sealed class DumpService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string FindRulesDir()
    {
        // Prefer rules next to the running exe. Fallback to current dir/rules.
        var exeDir = AppContext.BaseDirectory;
        var local = Path.Combine(exeDir, "rules");
        if (Directory.Exists(local) && File.Exists(Path.Combine(local, "ata-attributes.json")))
            return local;

        var cwd = Path.Combine(Directory.GetCurrentDirectory(), "rules");
        if (Directory.Exists(cwd) && File.Exists(Path.Combine(cwd, "ata-attributes.json")))
            return cwd;

        throw new DirectoryNotFoundException(
            $"Rules directory not found. Looked in:\n  {local}\n  {cwd}\n" +
            $"Ensure rules/ is copied alongside Excalibur.exe.");
    }

    public DeviceDump Run()
    {
        var rulesDir = FindRulesDir();
        var engine = SmartRuleEngine.Load(rulesDir);

        var enumr = new DeviceEnumerator();
        var disks = enumr.EnumerateAll().ToList();

        foreach (var disk in disks)
        {
            EnrichWithSmart(disk, engine);
        }

        return new DeviceDump
        {
            Disks = disks,
            Elevated = IsElevated(),
            RulesVersion = engine.RulesVersion,
        };
    }

    public string RunAsJson()
    {
        var dump = Run();
        return JsonSerializer.Serialize(dump, JsonOpts);
    }

    private static void EnrichWithSmart(PhysicalDisk disk, SmartRuleEngine engine)
    {
        if ((StorageBusType)disk.BusTypeCode == StorageBusType.Nvme)
        {
            using var h = NativeMethods.OpenDevice(disk.DiskNumber);
            if (h.IsInvalid)
            {
                disk.SmartAccessNote = "Cannot open disk for NVMe query (needs elevation?)";
                return;
            }
            disk.NvmeHealth = NvmeHealthReader.Read(h);
            if (!disk.NvmeHealth.IsValid)
                disk.SmartAccessNote = disk.NvmeHealth.ReadError ?? "NVMe health unavailable";
            return;
        }

        using (var h = NativeMethods.OpenDevice(disk.DiskNumber))
        {
            if (h.IsInvalid)
            {
                disk.SmartAccessNote = "Cannot open disk (needs elevation?)";
                return;
            }
            var smart = AtaSmartReader.Read(h);
            if (smart.IsValid)
            {
                engine.Interpret(smart, disk);
                disk.AtaSmart = smart;
                return;
            }
            if ((StorageBusType)disk.BusTypeCode == StorageBusType.Usb)
                disk.SmartAccessNote = "SMART via USB bridge not supported by this bridge";
            else
                disk.SmartAccessNote = smart.ReadError ?? "SMART read failed";
        }
    }

    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}