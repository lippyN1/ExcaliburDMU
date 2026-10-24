using System.Text.Json;
using System.Text.Json.Serialization;
using Excalibur.Core;
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

        if (TryGetStringArg(args, "--lang", out var lang)) L.SetLanguage(lang);

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

        Console.Error.WriteLine(L.T("Error_UnknownArgs", string.Join(' ', args)));
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
            Console.Error.WriteLine(L.T("Error_DiskRequired"));
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

        if (blockMb is <= 0 or > 64) { Console.Error.WriteLine(L.T("Error_BlockMbRange")); return 2; }
        if (bucketCount <= 0 || bucketCount > 100000) { Console.Error.WriteLine(L.T("Error_BucketsRange")); return 2; }
        int? blockBytes = blockMb.HasValue ? blockMb.Value * 1024 * 1024 : (int?)null;

        var enumr = new DeviceEnumerator();
        var disks = enumr.EnumerateAll();
        var disk = disks.FirstOrDefault(d => d.DiskNumber == diskNumber);
        if (disk == null)
        {
            Console.Error.WriteLine(L.T("Error_DiskNotFound", diskNumber, string.Join(",", disks.Select(d => d.DiskNumber))));
            return 2;
        }

        if (SurfaceTestService.IsSystemDisk(disk) && !force)
        {
            Console.Error.WriteLine(L.T("Error_RefuseSystemDisk"));
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
            if (!File.Exists(loadPath)) { Console.Error.WriteLine(L.T("Error_ResumeNotFound", loadPath)); return 2; }
            resumeFrom = TestStateStore.TryLoad(loadPath);
            if (resumeFrom == null) { Console.Error.WriteLine(L.T("Error_ResumeNotFound", loadPath)); return 2; }
            if (resumeFrom.DiskNumber != diskNumber) { Console.Error.WriteLine(L.T("Error_DiskNotFound", resumeFrom.DiskNumber, "")); return 2; }
            Console.Error.WriteLine(L.T("Msg_ResumingFrom", resumeFrom.LastCompletedLba + 1, resumeFrom.EndLba));
        }
        else if (!discardState && File.Exists(statePath))
        {
            Console.Error.WriteLine(L.T("Error_StateFileExists", statePath));
            return 2;
        }

        if (discardState && File.Exists(statePath))
        {
            TestStateStore.Delete(statePath);
            Console.Error.WriteLine(L.T("Msg_DiscardedState", statePath));
        }

        Console.Error.WriteLine(L.T("Msg_SurfaceTestTitle"));
        Console.Error.WriteLine(L.T("Msg_DiskLabel", disk.DiskNumber, disk.Model ?? "?"));
        Console.Error.WriteLine(L.T("Msg_SerialLabel", disk.SerialNumber ?? "?"));
        Console.Error.WriteLine(L.T("Msg_CapacityLabel", FormatBytes((double)(disk.CapacityBytes ?? 0))));
        Console.Error.WriteLine(L.T("Msg_RangeLabel", startLba ?? 0, endLba?.ToString() ?? "end"));
        Console.Error.WriteLine(L.T("Msg_StateLabel", statePath));
        Console.Error.WriteLine(L.T("App_PressCtrlC"));
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
        Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; Console.Error.WriteLine(); Console.Error.WriteLine(L.T("Msg_Cancelling")); cts.Cancel(); };

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
        if (fullyDone && File.Exists(statePath)) { TestStateStore.Delete(statePath); Console.Error.WriteLine(L.T("Msg_StateRemoved")); }
        else if (result.Status == "cancelled") { Console.Error.WriteLine(L.T("Msg_ProgressSaved", statePath)); }

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
                Console.Error.WriteLine(L.T("Msg_HTMLReport", outPath));
            }
            catch (Exception ex) { Console.Error.WriteLine("HTML report failed: " + ex.Message); }
        }

        Console.WriteLine(JsonSerializer.Serialize(result, JsonOpts));
        return result.Status switch { "ok" => 0, "completed-with-errors" => 1, "cancelled" => 130, _ => 4 };
    }

    private static int RunCapacityTest(string[] args)
    {
        if (!TryGetIntArg(args, "--disk", out int diskNumber))
        {
            Console.Error.WriteLine(L.T("Error_DiskRequired"));
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
            Console.Error.WriteLine(L.T("Error_NoUnderstandDataLoss"));
            return 2;
        }

        if (!hasConfirm)
        {
            Console.Error.WriteLine(L.T("Error_ConfirmRequired"));
            return 2;
        }

        var enumr = new DeviceEnumerator();
        var disks = enumr.EnumerateAll();
        var disk = disks.FirstOrDefault(d => d.DiskNumber == diskNumber);
        if (disk == null) { Console.Error.WriteLine(L.T("Error_DiskNotFound", diskNumber, "")); return 2; }

        if (CapacityTestService.IsSystemDisk(disk))
        {
            Console.Error.WriteLine(L.T("Error_RefuseSystemDisk"));
            return 2;
        }

        if (string.IsNullOrEmpty(disk.SerialNumber))
        {
            Console.Error.WriteLine(L.T("Error_SerialEmpty"));
            return 2;
        }

        if (!string.Equals(disk.SerialNumber, confirmSerial, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(L.T("Error_SerialMismatch", confirmSerial, disk.SerialNumber));
            return 2;
        }

        Console.Error.WriteLine(L.T("Msg_CapTestTitle"));
        Console.Error.WriteLine(L.T("Msg_DiskLabel", disk.DiskNumber, disk.Model ?? "?"));
        Console.Error.WriteLine(L.T("Msg_SerialLabel", disk.SerialNumber));
        Console.Error.WriteLine(L.T("Msg_CapacityLabel", FormatBytes((double)(disk.CapacityBytes ?? 0))));
        Console.Error.WriteLine(L.T("Msg_ModeLabel", full ? L.T("Mode_FullScan") : L.T("Mode_MarkerOnly")));
        Console.Error.WriteLine(L.T("Msg_MarkerLabel", markerMb));
        Console.Error.WriteLine(L.T("Msg_PartitionLabel", preservePartitionTable ? L.T("Mode_Preserve") : L.T("Mode_Destroy")));
        Console.Error.WriteLine();
        Console.Error.WriteLine(L.T("Msg_CapTestWarnAllData"));
        Console.Error.WriteLine(L.T("Msg_CapTestTypeSerial"));
        var typed = Console.ReadLine();
        if (!string.Equals(typed?.Trim(), disk.SerialNumber, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(L.T("Error_SerialInteractiveMismatch"));
            return 2;
        }

        Console.Error.WriteLine();
        if (preservePartitionTable)
        {
            Console.Error.WriteLine(L.T("Msg_CapTestPreserve"));
            Console.Error.WriteLine(L.T("Msg_CapTestPreserveDetail"));
        }
        else
        {
            Console.Error.WriteLine(L.T("Msg_CapTestWillBeDestroyed"));
            Console.Error.WriteLine(L.T("Msg_CapTestReformatNeeded"));
        }
        Console.Error.WriteLine();
        Console.Error.WriteLine(L.T("Msg_CapTestStarting3"));
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
        Console.Error.WriteLine(L.T("Msg_Verdict", result.Verdict));
        foreach (var r in result.VerdictReasons) Console.Error.WriteLine("  - " + r);
        if (result.Warnings.Count > 0)
        {
            Console.Error.WriteLine(L.T("Msg_Warnings"));
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
                Console.Error.WriteLine(L.T("Msg_HTMLReport", outPath));
            }
            catch (Exception ex) { Console.Error.WriteLine("HTML failed: " + ex.Message); }
        }

        Console.WriteLine(JsonSerializer.Serialize(result, JsonOpts));
        return result.Verdict == "Healthy" ? 0 : 1;
    }

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
        Console.WriteLine(L.T("App_Title"));
        Console.WriteLine();
        Console.WriteLine(L.T("App_Usage"));
        Console.WriteLine(L.T("Help_DumpDevices"));
        Console.WriteLine(L.T("Help_TestSurface"));
        Console.WriteLine(L.T("Help_CapacityTest"));
        Console.WriteLine();
        Console.WriteLine(L.T("Help_TestSurfaceOptions"));
        Console.WriteLine(L.T("Help_Surface_Desc"));
        Console.WriteLine(L.T("Help_Disk_Desc"));
        Console.WriteLine(L.T("Help_StartLba_Desc"));
        Console.WriteLine(L.T("Help_EndLba_Desc"));
        Console.WriteLine(L.T("Help_BlockMb_Desc"));
        Console.WriteLine(L.T("Help_NoCalibrate_Desc"));
        Console.WriteLine(L.T("Help_Buckets_Desc"));
        Console.WriteLine(L.T("Help_Quick_Desc"));
        Console.WriteLine(L.T("Help_Force_Desc"));
        Console.WriteLine(L.T("Help_ExportHtml_Desc"));
        Console.WriteLine(L.T("Help_Resume_Desc"));
        Console.WriteLine(L.T("Help_DiscardState_Desc"));
        Console.WriteLine(L.T("Help_SaveDir_Desc"));
        Console.WriteLine(L.T("Help_Lang_Desc"));
        Console.WriteLine();
        Console.WriteLine(L.T("Help_CapacityTestOptions"));
        Console.WriteLine(L.T("Help_CapTest_Desc"));
        Console.WriteLine(L.T("Help_Disk_Desc"));
        Console.WriteLine(L.T("Help_Confirm_Desc"));
        Console.WriteLine(L.T("Help_Understand_Desc"));
        Console.WriteLine(L.T("Help_Full_Desc"));
        Console.WriteLine(L.T("Help_MarkerMb_Desc"));
        Console.WriteLine(L.T("Help_NoPreserve_Desc"));
        Console.WriteLine();
        Console.WriteLine(L.T("App_RunElevated"));
    }
}