using Excalibur.Core.Devices;
using Excalibur.Core.Models;

namespace Excalibur.Core.Services;

public sealed class SurfaceTestService
{
    public sealed class Options
    {
        public int DiskNumber { get; set; }
        public long? StartLba { get; set; }
        public long? EndLba { get; set; }
        public int? BlockSizeBytes { get; set; }
        public bool Quick { get; set; }
        public bool Force { get; set; }
        public bool NoCalibrate { get; set; }
        public int BucketCount { get; set; } = 1000;
        public string? StateFilePath { get; set; }
        public TestState? ResumeFrom { get; set; }
    }

    public sealed class ProgressEvent
    {
        public bool InCalibration { get; init; }
        public string? CalibrationStep { get; init; }
        public SurfaceTestReader.ProgressSnapshot? Snapshot { get; init; }
    }

    public SurfaceTestResult Run(
        PhysicalDisk disk,
        Options opts,
        Action<ProgressEvent>? onProgress,
        Action<TestState>? onStateCheckpoint,
        CancellationToken ct)
    {
        long sectorSize = disk.LogicalSectorSize ?? 512;
        long totalSectors = disk.CapacityBytes.HasValue ? (disk.CapacityBytes.Value / sectorSize) : 0;
        if (totalSectors == 0)
            throw new InvalidOperationException("Cannot determine disk capacity for disk " + disk.DiskNumber);

        long startLba;
        long endLba;
        int? blockSize = opts.BlockSizeBytes;
        int bucketCount = opts.BucketCount;
        TestState? resume = opts.ResumeFrom;

        if (resume != null)
        {
            // Resume overrides range and block size from saved state
            startLba = resume.StartLba;
            endLba = resume.EndLba;
            blockSize = resume.BlockSizeBytes;
            bucketCount = resume.BucketCount;
        }
        else
        {
            startLba = opts.StartLba ?? 0;
            endLba = opts.EndLba ?? (totalSectors - 1);

            if (opts.Quick && opts.StartLba == null && opts.EndLba == null)
            {
                long oneGbInSectors = 1024L * 1024 * 1024 / sectorSize;
                if (totalSectors > oneGbInSectors) { startLba = 0; endLba = oneGbInSectors - 1; }
            }

            if (startLba < 0) startLba = 0;
            if (endLba >= totalSectors) endLba = totalSectors - 1;
            if (endLba < startLba)
                throw new ArgumentException("Invalid range: endLba < startLba");
        }

        long availableSectors = endLba - startLba + 1;
        int chosenBlockSize = blockSize ?? (4 * 1024 * 1024);

        CalibrationInfo? calibrationInfo = null;
        if (resume != null)
        {
            calibrationInfo = new CalibrationInfo
            {
                Performed = false,
                ChosenBlockSizeBytes = resume.BlockSizeBytes,
                ChosenSpeedMbps = 0,
            };
        }
        else if (opts.BlockSizeBytes == null && !opts.NoCalibrate)
        {
            onProgress?.Invoke(new ProgressEvent { InCalibration = true, CalibrationStep = "starting" });
            var reader = new SurfaceTestReader();
            var calib = reader.Calibrate(disk.DiskNumber, (uint)sectorSize, startLba, availableSectors, ct);
            calibrationInfo = calib.Info;
            if (calib.Info.Performed && calib.ChosenBlockSizeBytes > 0)
                chosenBlockSize = calib.ChosenBlockSizeBytes;
            onProgress?.Invoke(new ProgressEvent { InCalibration = true, CalibrationStep = "done" });
        }
        else if (opts.NoCalibrate)
        {
            calibrationInfo = new CalibrationInfo
            {
                Performed = false,
                ChosenBlockSizeBytes = chosenBlockSize,
                ChosenSpeedMbps = 0,
            };
        }

        var readerMain = new SurfaceTestReader();
        var opts2 = new SurfaceTestReader.Options
        {
            StartLba = startLba,
            EndLba = endLba,
            BlockSizeBytes = chosenBlockSize,
            BucketCount = bucketCount,
        };

        return readerMain.Run(
            disk.DiskNumber,
            disk.Model,
            disk.SerialNumber,
            disk.LogicalSectorSize,
            opts2,
            calibrationInfo,
            resume,
            snap => onProgress?.Invoke(new ProgressEvent { InCalibration = false, Snapshot = snap }),
            onStateCheckpoint,
            ct);
    }

    public static bool IsSystemDisk(PhysicalDisk disk)
    {
        var sysDrive = Environment.GetEnvironmentVariable("SystemDrive");
        if (string.IsNullOrEmpty(sysDrive)) sysDrive = "C:";
        var sysMount = sysDrive + "\\";
        return disk.Volumes.Any(v => v.MountPoint.Equals(sysMount, StringComparison.OrdinalIgnoreCase));
    }
}