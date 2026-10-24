using System.Diagnostics;
using System.Runtime.InteropServices;
using Excalibur.Core.Models;
using Excalibur.Core.Native;

namespace Excalibur.Core.Devices;

public sealed class SurfaceTestReader
{
    public sealed class ProgressSnapshot
    {
        public long BytesRead { get; init; }
        public long BytesTotal { get; init; }
        public double InstantSpeedMbps { get; init; }
        public double AverageSpeedMbps { get; init; }
        public double MinSpeedMbps { get; init; }
        public double MaxSpeedMbps { get; init; }
        public int BadSectors { get; init; }
        public double PercentComplete { get; init; }
        public TimeSpan Elapsed { get; init; }
        public TimeSpan? Eta { get; init; }
        public double WorstSpeedMbps { get; init; }
        public long WorstLba { get; init; }
        public int CurrentBlockSizeBytes { get; init; }
    }

    public sealed class Options
    {
        public long StartLba { get; init; }
        public long EndLba { get; init; }
        public int BlockSizeBytes { get; init; } = 4 * 1024 * 1024;
        public int BucketCount { get; init; } = 1000;
    }

    public sealed class CalibrationResult
    {
        public CalibrationInfo Info { get; init; } = new();
        public int ChosenBlockSizeBytes { get; init; }
    }

    // ---- calibration ----

    public CalibrationResult Calibrate(
        int diskNumber,
        uint sectorSize,
        long startLba,
        long availableSectors,
        CancellationToken ct)
    {
        int[] candidates = { 128 * 1024, 1024 * 1024, 4 * 1024 * 1024 };
        var samples = new List<CalibrationSample>();
        int bestBlockSize = 4 * 1024 * 1024;
        double bestSpeed = -1;

        long calibSectors = 500L * 1024 * 1024 / sectorSize;
        if (calibSectors > availableSectors) calibSectors = availableSectors;
        long calibBytes = calibSectors * (long)sectorSize;
        if (calibBytes < 16L * 1024 * 1024)
        {
            return new CalibrationResult
            {
                Info = new CalibrationInfo
                {
                    Performed = false,
                    ChosenBlockSizeBytes = 4 * 1024 * 1024,
                    ChosenSpeedMbps = 0,
                    Samples = samples,
                },
                ChosenBlockSizeBytes = 4 * 1024 * 1024,
            };
        }

        var handle = NativeMethods.OpenDeviceForRead(diskNumber);
        if (handle.IsInvalid)
        {
            return new CalibrationResult
            {
                Info = new CalibrationInfo { Performed = false, ChosenBlockSizeBytes = 4 * 1024 * 1024 },
                ChosenBlockSizeBytes = 4 * 1024 * 1024,
            };
        }

        try
        {
            const int passes = 2;
            var bestPerBlock = new Dictionary<int, double>();

            for (int pass = 0; pass < passes && !ct.IsCancellationRequested; pass++)
            {
                foreach (int bs in candidates)
                {
                    if (ct.IsCancellationRequested) break;

                    long blocks = calibBytes / bs;
                    if (blocks == 0) continue;

                    IntPtr buf = NativeMethods.VirtualAlloc(IntPtr.Zero, (UIntPtr)bs,
                        NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
                    if (buf == IntPtr.Zero) continue;

                    try
                    {
                        long offset = startLba * (long)sectorSize;
                        if (!NativeMethods.SetFilePointerEx(handle, offset, out _, NativeMethods.FILE_BEGIN))
                            continue;

                        var sw = Stopwatch.StartNew();
                        long totalRead = 0;
                        for (long i = 0; i < blocks; i++)
                        {
                            if (ct.IsCancellationRequested) break;
                            if (!NativeMethods.ReadFile(handle, buf, (uint)bs, out uint read, IntPtr.Zero) || read != (uint)bs)
                            {
                                if (!NativeMethods.SetFilePointerEx(handle, offset + (i + 1) * bs, out _, NativeMethods.FILE_BEGIN))
                                    break;
                                continue;
                            }
                            totalRead += read;
                        }
                        sw.Stop();
                        double sec = sw.Elapsed.TotalSeconds;
                        if (totalRead == 0 || sec <= 0) continue;
                        double speed = (totalRead / 1_048_576.0) / sec;
                        if (!bestPerBlock.TryGetValue(bs, out double prev) || speed > prev)
                            bestPerBlock[bs] = speed;
                    }
                    finally
                    {
                        NativeMethods.VirtualFree(buf, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
                    }
                }
            }

            foreach (var bs in candidates)
            {
                if (bestPerBlock.TryGetValue(bs, out double speed))
                {
                    samples.Add(new CalibrationSample
                    {
                        BlockSizeBytes = bs,
                        SpeedMbps = speed,
                        ElapsedSeconds = (calibBytes / 1_048_576.0) / Math.Max(speed, 1),
                        Success = true,
                    });
                    if (speed > bestSpeed)
                    {
                        bestSpeed = speed;
                        bestBlockSize = bs;
                    }
                }
                else
                {
                    samples.Add(new CalibrationSample
                    {
                        BlockSizeBytes = bs,
                        SpeedMbps = 0,
                        ElapsedSeconds = 0,
                        Success = false,
                    });
                }
            }
        }
        finally
        {
            handle.Dispose();
        }

        return new CalibrationResult
        {
            Info = new CalibrationInfo
            {
                Performed = true,
                ChosenBlockSizeBytes = bestBlockSize,
                ChosenSpeedMbps = bestSpeed < 0 ? 0 : bestSpeed,
                Samples = samples,
            },
            ChosenBlockSizeBytes = bestBlockSize,
        };
    }

    // ---- main scan ----

    public SurfaceTestResult Run(
        int diskNumber,
        string? model,
        string? serial,
        uint? logicalSectorSize,
        Options opts,
        CalibrationInfo? calibration,
        Action<ProgressSnapshot>? onProgress,
        CancellationToken ct)
    {
        if (opts.BlockSizeBytes <= 0 || opts.BlockSizeBytes % 4096 != 0)
            throw new ArgumentException("blockSizeBytes must be a positive multiple of 4096");
        if (opts.EndLba < opts.StartLba)
            throw new ArgumentException("endLba must be >= startLba");
        if (opts.BucketCount <= 0 || opts.BucketCount > 100000)
            throw new ArgumentException("bucketCount must be 1..100000");

        uint sectorSize = logicalSectorSize ?? 512;
        if (sectorSize == 0) sectorSize = 512;

        long totalBytes = (opts.EndLba - opts.StartLba + 1) * (long)sectorSize;
        long blocksTotal = totalBytes / opts.BlockSizeBytes;
        if (blocksTotal == 0)
        {
            return new SurfaceTestResult
            {
                DiskNumber = diskNumber, Model = model, SerialNumber = serial,
                LogicalSectorSize = logicalSectorSize,
                BlockSizeBytes = opts.BlockSizeBytes,
                StartLba = opts.StartLba, EndLba = opts.EndLba,
                TotalBytes = totalBytes,
                StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow,
                Calibration = calibration,
                Status = "error", Error = "Range too small for one block",
            };
        }

        int bucketCount = (int)Math.Min(opts.BucketCount, Math.Max(1, blocksTotal));
        long bytesPerBucket = (totalBytes + bucketCount - 1) / bucketCount;
        bytesPerBucket = ((bytesPerBucket + opts.BlockSizeBytes - 1) / opts.BlockSizeBytes) * opts.BlockSizeBytes;
        bucketCount = (int)Math.Min(bucketCount, Math.Max(1, (totalBytes + bytesPerBucket - 1) / bytesPerBucket));

        var buckets = new BucketStats[bucketCount];
        for (int i = 0; i < bucketCount; i++)
        {
            long bStart = opts.StartLba * (long)sectorSize + i * bytesPerBucket;
            long bEnd = Math.Min(bStart + bytesPerBucket, opts.StartLba * (long)sectorSize + totalBytes);
            buckets[i] = new BucketStats
            {
                Index = i,
                BytesStart = bStart - opts.StartLba * (long)sectorSize,
                BytesEnd = bEnd - opts.StartLba * (long)sectorSize,
                LbaStart = opts.StartLba + (bStart - opts.StartLba * (long)sectorSize) / sectorSize,
                LbaEnd = opts.StartLba + (bEnd - opts.StartLba * (long)sectorSize) / sectorSize - 1,
            };
        }

        long bytesRead = 0;
        long blocksRead = 0;
        var badBlocks = new List<BadBlock>();
        int totalBadSectors = 0;

        var sw = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.UtcNow;
        long lastReportMs = 0;
        long lastReportBytes = 0;

        var handle = NativeMethods.OpenDeviceForRead(diskNumber);
        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            return new SurfaceTestResult
            {
                DiskNumber = diskNumber, Model = model, SerialNumber = serial,
                LogicalSectorSize = logicalSectorSize,
                BlockSizeBytes = opts.BlockSizeBytes,
                StartLba = opts.StartLba, EndLba = opts.EndLba,
                TotalBytes = totalBytes, BlocksTotal = blocksTotal,
                StartedAt = startedAt, FinishedAt = DateTimeOffset.UtcNow,
                Calibration = calibration,
                Status = "error",
                Error = "CreateFile failed, Win32Error=" + err + " (" + DescribeWin32(err) + ")",
            };
        }

        IntPtr buffer = IntPtr.Zero;
        try
        {
            long startOffset = opts.StartLba * (long)sectorSize;
            if (!NativeMethods.SetFilePointerEx(handle, startOffset, out _, NativeMethods.FILE_BEGIN))
            {
                int err = Marshal.GetLastWin32Error();
                return new SurfaceTestResult
                {
                    DiskNumber = diskNumber, Model = model, SerialNumber = serial,
                    LogicalSectorSize = logicalSectorSize,
                    BlockSizeBytes = opts.BlockSizeBytes,
                    StartLba = opts.StartLba, EndLba = opts.EndLba,
                    TotalBytes = totalBytes, BlocksTotal = blocksTotal,
                    StartedAt = startedAt, FinishedAt = DateTimeOffset.UtcNow,
                    Calibration = calibration,
                    Status = "error",
                    Error = "SetFilePointerEx failed, Win32Error=" + err,
                };
            }

            buffer = NativeMethods.VirtualAlloc(IntPtr.Zero, (UIntPtr)opts.BlockSizeBytes,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
            if (buffer == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                return new SurfaceTestResult
                {
                    DiskNumber = diskNumber, Model = model, SerialNumber = serial,
                    LogicalSectorSize = logicalSectorSize,
                    BlockSizeBytes = opts.BlockSizeBytes,
                    StartLba = opts.StartLba, EndLba = opts.EndLba,
                    TotalBytes = totalBytes, BlocksTotal = blocksTotal,
                    StartedAt = startedAt, FinishedAt = DateTimeOffset.UtcNow,
                    Calibration = calibration,
                    Status = "error",
                    Error = "VirtualAlloc failed, Win32Error=" + err,
                };
            }

            int currentBucketIdx = 0;
            long bytesIntoCurrentBucket = 0;

            for (long i = 0; i < blocksTotal; i++)
            {
                if (ct.IsCancellationRequested) break;

                long blockStartTicks = sw.ElapsedTicks;
                bool ok = NativeMethods.ReadFile(handle, buffer, (uint)opts.BlockSizeBytes, out uint readBytes, IntPtr.Zero);
                long blockElapsedTicks = sw.ElapsedTicks - blockStartTicks;

                long blockOffset = i * opts.BlockSizeBytes;

                if (!ok || readBytes != (uint)opts.BlockSizeBytes)
                {
                    int err = ok ? 0 : Marshal.GetLastWin32Error();
                    long blockLbaStart = opts.StartLba + (blockOffset / (long)sectorSize);
                    long blockLbaEnd = blockLbaStart + (opts.BlockSizeBytes / (long)sectorSize) - 1;
                    int sectors = opts.BlockSizeBytes / (int)sectorSize;
                    badBlocks.Add(new BadBlock
                    {
                        LbaStart = blockLbaStart,
                        LbaEnd = blockLbaEnd,
                        SectorCount = sectors,
                        Win32Error = err,
                        ErrorName = ok ? "ShortRead" : DescribeWin32(err),
                        Timestamp = DateTimeOffset.UtcNow,
                    });
                    totalBadSectors += sectors;

                    int bi = (int)Math.Min(bucketCount - 1, blockOffset / bytesPerBucket);
                    buckets[bi].BadSectors += sectors;

                    if (!NativeMethods.SetFilePointerEx(handle, (blockLbaEnd + 1) * (long)sectorSize, out _, NativeMethods.FILE_BEGIN))
                        break;
                }
                else
                {
                    bytesRead += readBytes;

                    double blockSec = blockElapsedTicks / (double)Stopwatch.Frequency;
                    long remaining = readBytes;
                    while (remaining > 0)
                    {
                        long roomInBucket = bytesPerBucket - bytesIntoCurrentBucket;
                        long take = Math.Min(remaining, roomInBucket);
                        double takeSec = blockSec * ((double)take / readBytes);

                        buckets[currentBucketIdx].BytesRead += take;
                        buckets[currentBucketIdx].ElapsedSeconds += takeSec;
                        buckets[currentBucketIdx].BlocksRead++;

                        bytesIntoCurrentBucket += take;
                        remaining -= take;

                        if (bytesIntoCurrentBucket >= bytesPerBucket && currentBucketIdx < bucketCount - 1)
                        {
                            currentBucketIdx++;
                            bytesIntoCurrentBucket = 0;
                        }
                    }
                }

                blocksRead++;

                long nowMs = sw.ElapsedMilliseconds;
                if (onProgress != null && (nowMs - lastReportMs) >= 250)
                {
                    double nowSec = nowMs / 1000.0;
                    double deltaSec = (nowMs - lastReportMs) / 1000.0;
                    double deltaBytes = bytesRead - lastReportBytes;
                    double instantMbps = deltaSec > 0 ? (deltaBytes / 1_048_576.0) / deltaSec : 0;
                    double avgMbps = nowSec > 0 ? (bytesRead / 1_048_576.0) / nowSec : 0;

                    double pct = blocksTotal > 0 ? (100.0 * blocksRead / blocksTotal) : 0;
                    TimeSpan? eta = null;
                    if (avgMbps > 0 && blocksTotal > blocksRead)
                    {
                        double remainBytes = (blocksTotal - blocksRead) * (double)opts.BlockSizeBytes;
                        double remainSec = (remainBytes / 1_048_576.0) / avgMbps;
                        eta = TimeSpan.FromSeconds(remainSec);
                    }

                    onProgress(new ProgressSnapshot
                    {
                        BytesRead = bytesRead,
                        BytesTotal = totalBytes,
                        InstantSpeedMbps = instantMbps,
                        AverageSpeedMbps = avgMbps,
                        MinSpeedMbps = 0,
                        MaxSpeedMbps = 0,
                        BadSectors = totalBadSectors,
                        PercentComplete = pct,
                        Elapsed = sw.Elapsed,
                        Eta = eta,
                        WorstSpeedMbps = 0,
                        WorstLba = -1,
                        CurrentBlockSizeBytes = opts.BlockSizeBytes,
                    });

                    lastReportMs = nowMs;
                    lastReportBytes = bytesRead;
                }
            }

            double totalElapsedSec = sw.Elapsed.TotalSeconds;

            // Aggregate from buckets
            double finalAvg = 0;
            double minB = double.MaxValue;
            double maxB = 0;
            double worstB = double.MaxValue;
            long worstLba = -1;
            int withData = 0;
            double sumB = 0;
            var bucketAvgs = new List<double>();
            foreach (var b in buckets)
            {
                if (b.HasData)
                {
                    double avg = b.AvgSpeedMbps;
                    bucketAvgs.Add(avg);
                    sumB += avg;
                    withData++;
                    if (avg < minB) minB = avg;
                    if (avg > maxB) maxB = avg;
                    if (avg < worstB) { worstB = avg; worstLba = b.LbaStart; }
                }
            }
            if (withData > 0) finalAvg = sumB / withData;
            if (minB == double.MaxValue) minB = 0;
            if (worstB == double.MaxValue) { worstB = 0; worstLba = 0; }

            // Median + slow bucket count
            double medianSpeed = 0;
            int slowBucketCount = 0;
            if (bucketAvgs.Count > 0)
            {
                bucketAvgs.Sort();
                medianSpeed = bucketAvgs[bucketAvgs.Count / 2];
                double threshold = medianSpeed * 0.5;
                foreach (var b in buckets)
                    if (b.HasData && b.AvgSpeedMbps < threshold) slowBucketCount++;
            }

            string status = ct.IsCancellationRequested ? "cancelled" : (badBlocks.Count > 0 ? "completed-with-errors" : "ok");

            return new SurfaceTestResult
            {
                DiskNumber = diskNumber,
                Model = model,
                SerialNumber = serial,
                LogicalSectorSize = logicalSectorSize,
                BlockSizeBytes = opts.BlockSizeBytes,
                StartLba = opts.StartLba,
                EndLba = opts.EndLba,
                TotalBytes = totalBytes,
                StartedAt = startedAt,
                FinishedAt = DateTimeOffset.UtcNow,
                ElapsedSeconds = totalElapsedSec,
                AverageSpeedMbps = finalAvg,
                MinSpeedMbps = minB,
                MaxSpeedMbps = maxB,
                MedianSpeedMbps = medianSpeed,
                WorstLba = worstLba,
                WorstSpeedMbps = worstB,
                SlowBlockCount = 0,
                SlowBucketCount = slowBucketCount,
                BlocksRead = blocksRead,
                BlocksTotal = blocksTotal,
                BytesRead = bytesRead,
                BadBlocks = badBlocks,
                TotalBadSectors = totalBadSectors,
                Calibration = calibration,
                Map = new SurfaceMap
                {
                    BucketSizeBytes = bytesPerBucket,
                    BucketSizeMb = bytesPerBucket / (1024 * 1024),
                    BucketCount = bucketCount,
                    Buckets = buckets.ToList(),
                },
                Status = status,
            };
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                NativeMethods.VirtualFree(buffer, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
            handle.Dispose();
        }
    }

    private static string DescribeWin32(int err) => err switch
    {
        0 => "OK",
        5 => "AccessDenied",
        23 => "CRC",
        27 => "SectorNotFound",
        33 => "LockViolation",
        1117 => "IODeviceError",
        1167 => "DeviceNotConnected",
        1392 => "FileCorrupt",
        _ => "Win32_" + err,
    };
}