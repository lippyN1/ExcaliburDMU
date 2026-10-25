using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Excalibur.Core.Devices;
using Excalibur.Core.Models;
using Excalibur.Core.Services;
using Excalibur.Core.Dump;

namespace Excalibur.Cli
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Contains("--dump-devices"))
            {
                var dump = new DumpService();
                Console.WriteLine(dump.RunAsJson()); // <--- ВОТ ЭТА СТРОКА БЫЛА ПРОПУЩЕНА
                return;
            }
            if (args.Contains("--test-surface")) { RunSurfaceTest(int.Parse(args[args.Length - 1])); return; }
            if (args.Contains("--test-capacity")) { RunCapacityTest(int.Parse(args[args.Length - 1])); return; }
            if (args.Contains("--recover")) { RunRecovery(int.Parse(args[args.Length - 1])); return; }
            if (args.Contains("--format")) { RunFormat(int.Parse(args[args.Length - 1])); return; }

            Console.WriteLine("Excalibur Core v1.0.0");
            Console.WriteLine("Usage: --dump-devices | --test-surface <n> | --test-capacity <n> | --recover <n> | --format <n>");
        }

        static void RunSurfaceTest(int diskIndex)
        {
            try
            {
                var reader = new SurfaceTestReader();
                var opts = new SurfaceTestReader.Options { BlockSizeBytes = 1024 * 1024, BucketCount = 100 };
                var result = reader.Run(diskIndex, "Unknown", "Unknown", 512, opts, null, null,
                    p => { Console.WriteLine(JsonSerializer.Serialize(new { progress = Math.Round(p.PercentComplete, 2), speed = Math.Round(p.InstantSpeedMbps, 2) })); Console.Out.Flush(); },
                    null, CancellationToken.None);
                Console.WriteLine("RESULT: " + JsonSerializer.Serialize(new { success = true, badSectors = result.TotalBadSectors, avgSpeed = Math.Round(result.AverageSpeedMbps, 2) }));
            }
            catch (Exception ex) { Console.WriteLine("ERROR: " + ex.Message); }
        }

static void RunCapacityTest(int diskIndex)
        {
            try
            {
                var disk = new DeviceEnumerator().EnumerateAll().FirstOrDefault(d => d.DiskNumber == diskIndex);
                if (disk == null) { Console.WriteLine("ERROR: Disk not found"); return; }
                
                // ВАЖНО: Используем сервис, который умеет отмонтировать тома!
                var service = new CapacityTestService();
                var opts = new CapacityTestService.Options
                {
                    DiskNumber = diskIndex,
                    FullScan = false,
                    MarkerSizeBytes = 1024 * 1024,
                    PreservePartitionTable = true
                };

                var result = service.Run(disk, opts,
                    p => { 
                        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { progress = System.Math.Round(p.PercentComplete, 2), phase = p.Phase })); 
                        Console.Out.Flush(); 
                    },
                    System.Threading.CancellationToken.None);

                Console.WriteLine("RESULT: " + System.Text.Json.JsonSerializer.Serialize(new { 
                    success = true, 
                    verdict = result.Verdict, 
                    reasons = result.VerdictReasons,
                    capacityBytes = result.DeclaredCapacityBytes,
                    writeSpeed = Math.Round(result.WriteSpeedMbps, 2),
                    readSpeed = Math.Round(result.ReadSpeedMbps, 2),
                    elapsed = Math.Round(result.ElapsedSeconds, 1)
                }));
            }
            catch (Exception ex) { Console.WriteLine("ERROR: " + ex.Message); }
        }

        static void RunRecovery(int diskIndex)
        {
            try
            {
                var svc = new DataRecoveryService();
                var res = svc.Run(new DataRecoveryService.Options { DiskNumber = diskIndex, OutDir = @"C:\Recovered" },
                    p => { Console.WriteLine(JsonSerializer.Serialize(new { progress = Math.Round(p.Percent, 2), found = p.FoundFiles })); Console.Out.Flush(); },
                    CancellationToken.None);
                Console.WriteLine("RESULT: " + JsonSerializer.Serialize(new { success = true, message = res }));
            }
            catch (Exception ex) { Console.WriteLine("ERROR: " + ex.Message); }
        }

        static void RunFormat(int diskIndex)
        {
            try
            {
                var res = PartitionManager.QuickFormatDisk(diskIndex, "NTFS");
                Console.WriteLine("RESULT: " + JsonSerializer.Serialize(new { success = res.Success, output = res.Success ? "Форматирование завершено" : res.Error }));
            }
            catch (Exception ex) { Console.WriteLine("ERROR: " + ex.Message); }
        }
    }
}
