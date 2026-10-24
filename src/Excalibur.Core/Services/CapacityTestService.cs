using Excalibur.Core.Devices;
using Excalibur.Core.Models;
using Excalibur.Core.Native;

namespace Excalibur.Core.Services;

public sealed class CapacityTestService
{
    public sealed class Options
    {
        public int DiskNumber { get; set; }
        public bool FullScan { get; set; }
        public int MarkerSizeBytes { get; set; } = 4 * 1024 * 1024;
        public bool PreservePartitionTable { get; set; } = true;
    }

    public CapacityTestResult Run(
        PhysicalDisk disk,
        Options opts,
        Action<CapacityTestWriter.ProgressEvent>? onProgress,
        CancellationToken ct)
    {
        if (disk.Volumes.Count == 0)
        {
            var writer = new CapacityTestWriter();
            return writer.Run(disk, opts.FullScan, opts.MarkerSizeBytes, opts.PreservePartitionTable, onProgress, ct);
        }

        var locked = new List<VolumeControl.LockedVolume>();
        try
        {
            foreach (var vol in disk.Volumes)
            {
                if (string.IsNullOrEmpty(vol.MountPoint)) continue;
                locked.Add(VolumeControl.LockAndDismount(vol.MountPoint));
            }

            var writer = new CapacityTestWriter();
            return writer.Run(disk, opts.FullScan, opts.MarkerSizeBytes, opts.PreservePartitionTable, onProgress, ct);
        }
        finally
        {
            foreach (var lv in locked) lv.Dispose();
        }
    }

    public static bool IsSystemDisk(PhysicalDisk disk)
    {
        var sysDrive = Environment.GetEnvironmentVariable("SystemDrive");
        if (string.IsNullOrEmpty(sysDrive)) sysDrive = "C:";
        var sysMount = sysDrive + "\\";
        return disk.Volumes.Any(v => v.MountPoint.Equals(sysMount, StringComparison.OrdinalIgnoreCase));
    }
}