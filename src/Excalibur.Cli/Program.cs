using System.Text.Json;
using System.Text.Json.Serialization;
using Excalibur.Core.Devices;
using Excalibur.Core.Dump;
using Excalibur.Core.Models;
using Excalibur.Core.Services;

namespace Excalibur.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length == 0 || HasArg(args, "-h") || HasArg(args, "--help"))
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        if (HasArg(args, "--dump-devices"))
            return RunDumpDevices(!HasArg(args, "--compact"));

        if (HasArg(args, "--test-surface"))
            return RunSurfaceTest(args);

        if (HasArg(args, "--capacity-test"))
            return RunCapacityTest(args);

        Console.Error.WriteLine("Unknown arguments: " + string.Join(' ', args));
        PrintHelp();
        return 2;
    }

    // ---- dump-devices ----

    private static int RunDumpDevices(bool pretty)
    {
        var service = new DumpService();
        DeviceDump dump;
        try { dump = service.Run(); }
        catch (Exception ex) { Console.Error.WriteLine("Fatal: " + ex); return 3; }

        var opts = new JsonSerializerOptions
        {
            WriteIndented = pretty,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        Console.WriteLine(JsonSerializer.Serialize(dump, opts));
        return 0;
    }

    // ---- test-surface ----

    private static int RunSurfaceTest(string[] args)
    {
        if (!TryGetIntArg(args, "--disk", out int diskNumber))
        {
            Console.Error.WriteLine("--disk N is required");
            return 2;
        }

        long? startLba = TryGetLongArg(args, "--start-lba", out var s) ? s : (long?)null;
        long? endLba = TryGetLongArg(args, "--end-lba", out var e) ? e : (long?)null;
        int? blockMb = TryGetIntArg(args, "--block-mb", out var b) ? b : (int?)null;
        int bucketCount = TryGetIntArg(args, "--buckets", out var bk) ? bk : 1000;
        bool quick = HasArg(args, "--quick");
        bool force = HasArg(args, "--force");
        bool noCalibrate = HasArg(args, "--no-calibrate");
        string? exportHtml = TryGetStringArg(args, "--export-html", out var eh) ? eh : null;
        string? saveDirOverride = TryGetStringArg(args, "--save-dir", out var sd) ? sd : null;
        bool resumeFlag = HasArg(args, "--resume");
        string? resumePath = TryGetStringArg(args, "--resume", out var rp) ? rp : null;
        bool discardState = HasArg(args, "--discard-state");

        if (blockMb is <= 0 or > 64) { Console.Error.WriteLine("--block-mb must be 1..64"); return 2; }
        if (bucketCount <= 0 || bucketCount > 100000) { Console.Error.WriteLine("--buckets must be 1..100000"); return 2; }
        int? blockBytes = blockMb.HasValue ? blockMb.Value * 1024 * 1024 : (int?)null;

        var enumr = new DeviceEnumerator();
        var disks = enumr.EnumerateAll();
        var disk = disks.FirstOrDefault(d => d.DiskNumber == diskNumber);
        if (disk == null)
        {
            Console.Error.WriteLine("Disk " + diskNumber + " not found. Available: " + string.Join(",", disks.Select(d => d.DiskNumber)));
            return 2;
        }

        if (SurfaceTestService.IsSystemDisk(disk) && !force)
        {
            Console.Error.WriteLine("Refusing to test system disk (contains Windows volume). Add --force.");
            return 2;
        }

        string statePath;
        if (!string.IsNullOrEmpty(saveDirOverride))
        {
            Directory.CreateDirectory(saveDirOverride);
            var key = string.IsNullOrEmpty(disk.SerialNumber) ? ("disk" + disk.DiskNumber) : disk.SerialNumber;
            foreach (var c in Path.GetInvalidFileNameChars()) key = key.Replace(c, '_');
            statePath = Path.Combine(saveDirOverride, key + ".json");
        }
        else
        {
            statePath = TestStateStore.DefaultStatePath(disk);
        }

        TestState? resumeFrom = null;
        if (resumeFlag)
        {
            var loadPath = string.IsNullOrEmpty(resumePath) ? statePath : resumePath;
            if (!File.Exists(loadPath)) { Console.Error.WriteLine("Resume requested, but state file not found: " + loadPath); return 2; }
            resumeFrom = TestStateStore.TryLoad(loadPath);
            if (resumeFrom == null) { Console.Error.WriteLine("Failed to load state file: " + loadPath); return 2; }
            if (resumeFrom.DiskNumber != diskNumber) { Console.Error.WriteLine("State file belongs to disk " + resumeFrom.DiskNumber); return 2; }
            Console.Error.WriteLine("Resuming from LBA " + (resumeFrom.LastCompletedLba + 1) + " / " + resumeFrom.EndLba);
        }
        else if (!discardState && File.Exists(statePath))
        {
            Console.Error.WriteLine("A saved state exists at " + statePath + ".");
            Console.Error.WriteLine("Use --resume to continue, or --discard-state to start fresh.");
            return 2;
        }

        if (discardState && File.Exists(statePath))
        {
            TestStateStore.Delete(statePath);
            Console.Error.WriteLine("Discarded previous state: " + statePath);
        }

        Console.Error.WriteLine("Excalibur surface test (read-only)");
        Console.Error.WriteLine("  Disk     : #" + disk.DiskNumber + " — " + (disk.Model ?? "?"));
        Console.Error.WriteLine("  Serial   : " + (disk.SerialNumber ?? "?"));
        Console.Error.WriteLine("  Capacity : " + FormatBytes((double)(disk.CapacityBytes ?? 0)));
        Console.Error.WriteLine("  Range    : LBA " + (startLba ?? 0) + ".." + (endLba?.ToString() ?? "end"));
        Console.Error.WriteLine("  State    : " + statePath);
        Console.Error.WriteLine("Press Ctrl+C to cancel (state saved automatically).");
        Console.Error.WriteLine();

        var opts = new SurfaceTestService.Options
        {
            DiskNumber = diskNumber,
            StartLba = startLba,
            EndLba = endLba,
            BlockSizeBytes = blockBytes,
            Quick = quick,
            Force = force,
            NoCalibrate = noCalibrate,
            BucketCount = bucketCount,
            StateFilePath = statePath,
            ResumeFrom = resumeFrom,
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; Console.Error.WriteLine(); Console.Error.WriteLine("Cancelling..."); cts.Cancel(); };

        var service = new SurfaceTestService();
        int lastLen = 0;
        SurfaceTestResult result;
        try
        {
            result = service.Run(disk, opts, pe =>
            {
                if (pe.InCalibration || pe.Snapshot == null) return;
                var p = pe.Snapshot;
                var line = string.Format(
                    "\r[{0,5:0.0}%] {1,9} / {2,-9} | {3,6:0.0} MB/s (avg {4,6:0.0}) | bad: {5,3} | ETA {6}",
                    p.PercentComplete, FormatBytes((double)p.BytesRead), FormatBytes((double)p.BytesTotal),
                    p.InstantSpeedMbps, p.AverageSpeedMbps, p.BadSectors, FormatEta(p.Eta));
                Console.Error.Write(line);
                if (line.Length < lastLen) Console.Error.Write(new string(' ', lastLen - line.Length));
                lastLen = line.Length;
            }, st => { try { TestStateStore.Save(statePath, st); } catch (Exception ex) { Console.Error.WriteLine(); Console.Error.WriteLine("WARN: state save failed: " + ex.Message); } }, cts.Token);
        }
        catch (Exception ex) { Console.Error.WriteLine(); Console.Error.WriteLine("Fatal: " + ex); return 3; }

        Console.Error.WriteLine();
        Console.Error.WriteLine();

        bool fullyDone = result.Status == "ok" || result.Status == "completed-with-errors";
        if (fullyDone && File.Exists(statePath)) { TestStateStore.Delete(statePath); Console.Error.WriteLine("State file removed (test completed)."); }
        else if (result.Status == "cancelled") { Console.Error.WriteLine("Progress saved to: " + statePath); }

        if (!string.IsNullOrWhiteSpace(exportHtml))
        {
            try
            {
                string? rulesVer = null;
                try
                {
                    var rulesPath = Path.Combine(AppContext.BaseDirectory, "rules", "rules-version.txt");
                    if (File.Exists(rulesPath)) rulesVer = File.ReadAllText(rulesPath).Trim();
                }
                catch { }
                var html = HtmlReportGenerator.Generate(result, rulesVer);
                var outPath = Path.GetFullPath(exportHtml);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
                File.WriteAllText(outPath, html, new System.Text.UTF8Encoding(false));
                Console.Error.WriteLine("HTML report: " + outPath);
            }
            catch (Exception ex) { Console.Error.WriteLine("HTML report failed: " + ex.Message); }
        }

        Console.WriteLine(JsonSerializer.Serialize(result, JsonOpts));
        return result.Status switch { "ok" => 0, "completed-with-errors" => 1, "cancelled" => 130, _ => 4 };
    }

    // ---- capacity-test ----

    private static int RunCapacityTest(string[] args)
    {
        if (!TryGetIntArg(args, "--disk", out int diskNumber))
        {
            Console.Error.WriteLine("--disk N is required");
            return 2;
        }

        bool hasConfirm = TryGetStringArg(args, "--confirm", out var confirmSerial);
        bool hasUnderstand = HasArg(args, "--i-understand-data-loss");
        bool full = HasArg(args, "--full");
        int markerMb = TryGetIntArg(args, "--marker-mb", out var mm) ? mm : 4;
        int markerSize = markerMb * 1024 * 1024;
        string? exportHtml = TryGetStringArg(args, "--export-html", out var eh) ? eh : null;
        bool preservePartitionTable = !HasArg(args, "--no-preserve-partition-table");

        if (!hasUnderstand)
        {
            Console.Error.WriteLine("DESTRUCTIVE OPERATION.");
            Console.Error.WriteLine("This test writes to the disk and destroys all data on it.");
            Console.Error.WriteLine("Add --i-understand-data-loss to confirm.");
            return 2;
        }

        if (!hasConfirm)
        {
            Console.Error.WriteLine("--confirm <SERIAL> is required. Type the exact disk serial number.");
            return 2;
        }

        var enumr = new DeviceEnumerator();
        var disks = enumr.EnumerateAll();
        var disk = disks.FirstOrDefault(d => d.DiskNumber == diskNumber);
        if (disk == null) { Console.Error.WriteLine("Disk not found"); return 2; }

        if (CapacityTestService.IsSystemDisk(disk))
        {
            Console.Error.WriteLine("Refusing to run capacity test on the system disk (Windows volume).");
            return 2;
        }

        if (string.IsNullOrEmpty(disk.SerialNumber))
        {
            Console.Error.WriteLine("Disk has no serial number; cannot safely confirm. Aborting.");
            return 2;
        }

        if (!string.Equals(disk.SerialNumber, confirmSerial, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("SERIAL MISMATCH: --confirm '" + confirmSerial + "' does not match '" + disk.SerialNumber + "'");
            return 2;
        }

        Console.Error.WriteLine("================= DESTRUCTIVE OPERATION =================");
        Console.Error.WriteLine("Disk     : #" + disk.DiskNumber + " — " + (disk.Model ?? "?"));
        Console.Error.WriteLine("Serial   : " + disk.SerialNumber);
        Console.Error.WriteLine("Capacity : " + FormatBytes((double)(disk.CapacityBytes ?? 0)));
        Console.Error.WriteLine("Mode     : " + (full ? "FULL SCAN (hours)" : "marker-only (fast)"));
        Console.Error.WriteLine("Marker   : " + markerMb + " MB");
        Console.Error.WriteLine("Partition: " + (preservePartitionTable ? "will be preserved (MBR/GPT saved and restored)" : "will be DESTROYED"));
        Console.Error.WriteLine();
        Console.Error.WriteLine("ALL DATA ON THIS DISK WILL BE DESTROYED.");
        Console.Error.WriteLine("Type the serial number again to proceed (or Ctrl+C to abort):");
        var typed = Console.ReadLine();
        if (!string.Equals(typed?.Trim(), disk.SerialNumber, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Serial mismatch on interactive prompt. Aborting.");
            return 2;
        }

        if (preservePartitionTable)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Partition table (MBR/GPT) will be preserved.");
            Console.Error.WriteLine("Filesystem data will still be damaged (only markers are written),");
            Console.Error.WriteLine("but the volume will remain mountable (files will appear corrupted).");
            Console.Error.WriteLine("To fully wipe the disk, use --no-preserve-partition-table.");
        }
        else
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("WARNING: --no-preserve-partition-table — the entire disk layout will be destroyed.");
            Console.Error.WriteLine("After the test you will need to reformat the disk in Windows.");
        }
        Console.Error.WriteLine();
        Console.Error.WriteLine("Starting in 3 seconds... Ctrl+C to abort.");
        for (int i = 3; i > 0; i--) { Console.Error.WriteLine("  " + i + "..."); Thread.Sleep(1000); }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; cts.Cancel(); };

        var service = new CapacityTestService();
        CapacityTestResult result;
        try
        {
            result = service.Run(disk, new CapacityTestService.Options
            {
                DiskNumber = diskNumber,
                FullScan = full,
                MarkerSizeBytes = markerSize,
                PreservePartitionTable = preservePartitionTable,
            }, pe => { Console.Error.Write("\r[" + pe.Phase + "] " + pe.PercentComplete.ToString("0.0") + "% " + (pe.Message ?? "")); }, cts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Fatal: " + ex);
            return 3;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine();
        Console.Error.WriteLine("Verdict: " + result.Verdict);
        foreach (var r in result.VerdictReasons) Console.Error.WriteLine("  - " + r);
        if (result.Warnings.Count > 0)
        {
            Console.Error.WriteLine("Warnings:");
            foreach (var w in result.Warnings) Console.Error.WriteLine("  - " + w);
        }

        if (!string.IsNullOrWhiteSpace(exportHtml))
        {
            try
            {
                var html = CapacityHtmlReportGenerator.Generate(result, null);
                var outPath = Path.GetFullPath(exportHtml);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
                File.WriteAllText(outPath, html, new System.Text.UTF8Encoding(false));
                Console.Error.WriteLine("HTML report: " + outPath);
            }
            catch (Exception ex) { Console.Error.WriteLine("HTML failed: " + ex.Message); }
        }

        Console.WriteLine(JsonSerializer.Serialize(result, JsonOpts));
        return result.Verdict == "Healthy" ? 0 : 1;
    }

    // ---- helpers ----

    private static bool HasArg(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetIntArg(string[] args, string name, out int value)
    {
        value = 0;
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out value)) return true;
        return false;
    }

    private static bool TryGetLongArg(string[] args, string name, out long value)
    {
        value = 0;
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && long.TryParse(args[i + 1], out value)) return true;
        return false;
    }

    private static bool TryGetStringArg(string[] args, string name, out string value)
    {
        value = "";
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) { value = args[i + 1]; return !string.IsNullOrEmpty(value); }
        return false;
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        int i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        return bytes.ToString("N2") + " " + units[i];
    }

    private static string FormatEta(TimeSpan? eta)
    {
        if (eta == null) return "  --:--";
        var t = eta.Value;
        if (t.TotalHours >= 1) return string.Format("{0:0}h{1:00}m", (int)t.TotalHours, t.Minutes);
        if (t.TotalMinutes >= 1) return string.Format("{0:0}m{1:00}s", (int)t.TotalMinutes, t.Seconds);
        return string.Format("{0:0}s", (int)t.TotalSeconds);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Excalibur v1.0.0-alpha — portable disk diagnostic tool");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  Excalibur.exe --dump-devices [--compact]");
        Console.WriteLine("  Excalibur.exe --test-surface --disk N [options]");
        Console.WriteLine("  Excalibur.exe --capacity-test --disk N --confirm SERIAL --i-understand-data-loss [options]");
        Console.WriteLine();
        Console.WriteLine("test-surface:");
        Console.WriteLine("  --test-surface        Read-only scan");
        Console.WriteLine("  --disk N              Physical disk number");
        Console.WriteLine("  --start-lba X         Start LBA");
        Console.WriteLine("  --end-lba Y           End LBA");
        Console.WriteLine("  --block-mb B          Block size, MB");
        Console.WriteLine("  --no-calibrate        Skip calibration");
        Console.WriteLine("  --buckets N           Map buckets (default 1000)");
        Console.WriteLine("  --quick               First 1 GB only");
        Console.WriteLine("  --force               Allow system disk");
        Console.WriteLine("  --export-html P       Write HTML report");
        Console.WriteLine("  --resume [P]          Resume from saved state");
        Console.WriteLine("  --discard-state       Delete saved state");
        Console.WriteLine("  --save-dir D          Override state directory");
        Console.WriteLine();
        Console.WriteLine("capacity-test (DESTRUCTIVE):");
        Console.WriteLine("  --capacity-test                 Detect fake capacity");
        Console.WriteLine("  --disk N                        Physical disk number");
        Console.WriteLine("  --confirm SERIAL                Exact disk serial (required)");
        Console.WriteLine("  --i-understand-data-loss        Required to proceed");
        Console.WriteLine("  --full                          Full write+verify (slow)");
        Console.WriteLine("  --marker-mb B                   Marker block size (default 4)");
        Console.WriteLine("  --export-html P                 Write HTML report");
        Console.WriteLine("  --no-preserve-partition-table   Do NOT save/restore MBR/GPT (default: preserve)");
    }
}