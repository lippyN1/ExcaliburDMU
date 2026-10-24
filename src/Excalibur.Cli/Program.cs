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

        Console.Error.WriteLine("Unknown arguments: " + string.Join(' ', args));
        PrintHelp();
        return 2;
    }

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

    private static int RunSurfaceTest(string[] args)
    {
        // Parse arguments
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

        // Find disk
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
            Console.Error.WriteLine("Refusing to test system disk (disk " + diskNumber + " contains Windows volume).");
            Console.Error.WriteLine("Add --force to proceed anyway.");
            return 2;
        }

        // Resolve state file path
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

        // Resume logic
        TestState? resumeFrom = null;
        if (resumeFlag)
        {
            var loadPath = string.IsNullOrEmpty(resumePath) ? statePath : resumePath;
            if (!File.Exists(loadPath))
            {
                Console.Error.WriteLine("Resume requested, but state file not found: " + loadPath);
                return 2;
            }
            resumeFrom = TestStateStore.TryLoad(loadPath);
            if (resumeFrom == null)
            {
                Console.Error.WriteLine("Failed to load state file: " + loadPath);
                return 2;
            }
            if (resumeFrom.DiskNumber != diskNumber)
            {
                Console.Error.WriteLine("State file belongs to disk " + resumeFrom.DiskNumber + ", but --disk " + diskNumber + " given.");
                return 2;
            }
            if (!string.Equals(resumeFrom.SerialNumber, disk.SerialNumber, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("WARNING: disk serial in state (" + resumeFrom.SerialNumber + ") differs from current (" + disk.SerialNumber + ").");
                Console.Error.WriteLine("Continuing anyway in 3 seconds... Press Ctrl+C to abort.");
                Thread.Sleep(3000);
            }
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
        Console.Error.WriteLine("  Disk     : " + disk.DiskNumber + " — " + (disk.Model ?? "?"));
        Console.Error.WriteLine("  Serial   : " + (disk.SerialNumber ?? "?"));
        Console.Error.WriteLine("  Bus      : " + disk.BusTypeName);
        Console.Error.WriteLine("  Capacity : " + FormatBytes((double)(disk.CapacityBytes ?? 0)));
        Console.Error.WriteLine("  Sector   : " + (disk.LogicalSectorSize?.ToString() ?? "?") + " B");
        Console.Error.WriteLine("  Block    : " + (blockBytes.HasValue ? (blockBytes.Value / 1024 / 1024) + " MB (fixed)" : "auto (calibrate)"));
        Console.Error.WriteLine("  Buckets  : " + bucketCount);
        Console.Error.WriteLine("  Range    : LBA " + (startLba ?? 0) + ".." + (endLba?.ToString() ?? "end"));
        if (quick) Console.Error.WriteLine("  Mode     : quick (first 1 GB)");
        if (exportHtml != null) Console.Error.WriteLine("  HTML     : " + exportHtml);
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
        bool cancelRequested = false;
        Console.CancelKeyPress += (_, ev) =>
        {
            ev.Cancel = true;
            if (!cancelRequested)
            {
                cancelRequested = true;
                Console.Error.WriteLine();
                Console.Error.WriteLine("Cancelling... state will be saved on exit.");
                cts.Cancel();
            }
        };

        var service = new SurfaceTestService();
        int lastLineLen = 0;
        bool inCalibration = false;

        Action<TestState> saveState = st =>
        {
            try
            {
                TestStateStore.Save(statePath, st);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("WARN: state save failed: " + ex.Message);
            }
        };

        SurfaceTestResult result;
        try
        {
            result = service.Run(disk, opts, pe =>
            {
                if (pe.InCalibration)
                {
                    if (!inCalibration)
                    {
                        Console.Error.WriteLine("Calibrating block size (500 MB probe)...");
                        inCalibration = true;
                    }
                    return;
                }
                inCalibration = false;
                var p = pe.Snapshot;
                if (p == null) return;

                var line = string.Format(
                    "\r[{0,5:0.0}%] {1,9} / {2,-9} | {3,6:0.0} MB/s (avg {4,6:0.0}) | bad: {5,3} | ETA {6}",
                    p.PercentComplete,
                    FormatBytes((double)p.BytesRead),
                    FormatBytes((double)p.BytesTotal),
                    p.InstantSpeedMbps,
                    p.AverageSpeedMbps,
                    p.BadSectors,
                    FormatEta(p.Eta));
                Console.Error.Write(line);
                if (line.Length < lastLineLen)
                    Console.Error.Write(new string(' ', lastLineLen - line.Length));
                lastLineLen = line.Length;
            }, saveState, cts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Fatal: " + ex);
            return 3;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine();

        // Clean up state on successful completion (only if finished the full range)
        bool fullyDone = result.Status == "ok" || result.Status == "completed-with-errors";
        if (fullyDone && File.Exists(statePath))
        {
            TestStateStore.Delete(statePath);
            Console.Error.WriteLine("State file removed (test completed).");
        }
        else if (result.Status == "cancelled")
        {
            Console.Error.WriteLine("Progress saved to: " + statePath);
            Console.Error.WriteLine("Resume with: Excalibur.exe --test-surface --disk " + diskNumber + " --resume");
        }

        // HTML report
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
            catch (Exception ex)
            {
                Console.Error.WriteLine("HTML report failed: " + ex.Message);
            }
        }

        var json = JsonSerializer.Serialize(result, JsonOpts);
        Console.WriteLine(json);

        return result.Status switch
        {
            "ok" => 0,
            "completed-with-errors" => 1,
            "cancelled" => 130,
            _ => 4,
        };
    }

    private static bool HasArg(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetIntArg(string[] args, string name, out int value)
    {
        value = 0;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out value)) return true;
        }
        return false;
    }

    private static bool TryGetLongArg(string[] args, string name, out long value)
    {
        value = 0;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(args[i + 1], out value)) return true;
        }
        return false;
    }

    private static bool TryGetStringArg(string[] args, string name, out string value)
    {
        value = "";
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                value = args[i + 1];
                return !string.IsNullOrEmpty(value);
            }
        }
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
        Console.WriteLine();
        Console.WriteLine("dump-devices options:");
        Console.WriteLine("  --dump-devices    Enumerate disks, output JSON");
        Console.WriteLine("  --compact         Compact JSON");
        Console.WriteLine();
        Console.WriteLine("test-surface options:");
        Console.WriteLine("  --test-surface    Read-only surface scan of physical disk N");
        Console.WriteLine("  --disk N          Physical disk number (0, 1, ...)");
        Console.WriteLine("  --start-lba X     Start LBA (default: 0)");
        Console.WriteLine("  --end-lba Y       End LBA (default: end of disk)");
        Console.WriteLine("  --block-mb B      Block size in MB, 1..64 (default: auto-calibrate)");
        Console.WriteLine("  --no-calibrate    Skip calibration, use 4 MB blocks");
        Console.WriteLine("  --buckets N       Number of map buckets (default: 1000)");
        Console.WriteLine("  --quick           First 1 GB only");
        Console.WriteLine("  --force           Allow testing system disk");
        Console.WriteLine("  --export-html P   Write self-contained HTML report to path P");
        Console.WriteLine("  --resume [P]      Resume from saved state (P optional; default: state dir)");
        Console.WriteLine("  --discard-state   Discard any previous state before starting");
        Console.WriteLine("  --save-dir D      Override state directory");
        Console.WriteLine();
        Console.WriteLine("State is auto-saved every 5 s to %LOCALAPPDATA%\\Excalibur\\test-state\\<serial>.json");
        Console.WriteLine("and removed on successful completion. Ctrl+C saves and exits.");
        Console.WriteLine();
        Console.WriteLine("Exit codes:");
        Console.WriteLine("  0 = no errors, 1 = bad sectors, 2 = bad args, 4 = fatal, 130 = cancelled");
    }
}