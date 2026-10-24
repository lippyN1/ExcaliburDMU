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
            Console.Error.WriteLine("Refusing to test system disk (disk " + diskNumber + " contains Windows volume).");
            Console.Error.WriteLine("Add --force to proceed anyway.");
            return 2;
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
        Console.Error.WriteLine("Press Ctrl+C to cancel.");
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
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, ev) =>
        {
            ev.Cancel = true;
            Console.Error.WriteLine();
            Console.Error.WriteLine("Cancelling...");
            cts.Cancel();
        };

        var service = new SurfaceTestService();
        int lastLineLen = 0;
        bool inCalibration = false;

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

                var worst = p.WorstLba >= 0
                    ? string.Format("worst {0,5:0.0} @ LBA {1}", p.WorstSpeedMbps, p.WorstLba)
                    : "worst --";
                var line = string.Format(
                    "\r[{0,5:0.0}%] {1,9} / {2,-9} | {3,6:0.0} MB/s (avg {4,6:0.0}, min {5,5:0.0}, max {6,5:0.0}) | {7} | bad: {8,3} | ETA {9}",
                    p.PercentComplete,
                    FormatBytes((double)p.BytesRead),
                    FormatBytes((double)p.BytesTotal),
                    p.InstantSpeedMbps,
                    p.AverageSpeedMbps,
                    p.MinSpeedMbps,
                    p.MaxSpeedMbps,
                    worst,
                    p.BadSectors,
                    FormatEta(p.Eta));
                Console.Error.Write(line);
                if (line.Length < lastLineLen)
                    Console.Error.Write(new string(' ', lastLineLen - line.Length));
                lastLineLen = line.Length;
            }, cts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Fatal: " + ex);
            return 3;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine();

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

                result = new SurfaceTestResult
                {
                    DiskNumber = result.DiskNumber,
                    Model = result.Model,
                    SerialNumber = result.SerialNumber,
                    LogicalSectorSize = result.LogicalSectorSize,
                    StartLba = result.StartLba,
                    EndLba = result.EndLba,
                    TotalBytes = result.TotalBytes,
                    BlockSizeBytes = result.BlockSizeBytes,
                    StartedAt = result.StartedAt,
                    FinishedAt = result.FinishedAt,
                    ElapsedSeconds = result.ElapsedSeconds,
                    BlocksTotal = result.BlocksTotal,
                    BlocksRead = result.BlocksRead,
                    BytesRead = result.BytesRead,
                    AverageSpeedMbps = result.AverageSpeedMbps,
                    MinSpeedMbps = result.MinSpeedMbps,
                    MaxSpeedMbps = result.MaxSpeedMbps,
                    WorstLba = result.WorstLba,
                    WorstSpeedMbps = result.WorstSpeedMbps,
                    BadBlocks = result.BadBlocks,
                    TotalBadSectors = result.TotalBadSectors,
                    SlowBlockCount = result.SlowBlockCount,
                    MedianSpeedMbps = result.MedianSpeedMbps,
                    SlowBucketCount = result.SlowBucketCount,
                    Calibration = result.Calibration,
                    Map = result.Map,
                    HtmlReportPath = outPath,
                    Status = result.Status,
                    Error = result.Error,
                };
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
                int.TryParse(args[i + 1], out value))
                return true;
        }
        return false;
    }

    private static bool TryGetLongArg(string[] args, string name, out long value)
    {
        value = 0;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(args[i + 1], out value))
                return true;
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
        while (bytes >= 1024 && i < units.Length - 1)
        {
            bytes /= 1024;
            i++;
        }
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
        Console.WriteLine("  --dump-devices   Enumerate physical disks, dump info + SMART/health as JSON");
        Console.WriteLine("  --compact        Compact JSON (single line)");
        Console.WriteLine();
        Console.WriteLine("test-surface options:");
        Console.WriteLine("  --test-surface   Read-only surface scan of physical disk N");
        Console.WriteLine("  --disk N         Physical disk number (0, 1, 2...)");
        Console.WriteLine("  --start-lba X    Start LBA (default: 0)");
        Console.WriteLine("  --end-lba Y      End LBA (default: end of disk)");
        Console.WriteLine("  --block-mb B     Block size in MB, 1..64 (default: auto-calibrate, usually 4 MB)");
        Console.WriteLine("  --no-calibrate   Skip calibration, use 4 MB blocks");
        Console.WriteLine("  --buckets N      Number of map buckets (default: 1000)");
        Console.WriteLine("  --quick          Test only first 1 GB (fast sanity check)");
        Console.WriteLine("  --force          Allow testing the system disk (C:)");
        Console.WriteLine("  --export-html P  Write a self-contained HTML report to path P");
        Console.WriteLine();
        Console.WriteLine("Exit codes for --test-surface:");
        Console.WriteLine("  0   no errors found");
        Console.WriteLine("  1   bad sectors found");
        Console.WriteLine("  2   bad arguments");
        Console.WriteLine("  4   fatal error (device disconnected, etc.)");
        Console.WriteLine("  130 cancelled by user (Ctrl+C)");
        Console.WriteLine();
        Console.WriteLine("Run elevated (Administrator) to access disks.");
    }
}