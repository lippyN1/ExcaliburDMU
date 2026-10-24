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
        public int BadSectors { get; init; }
        public int RepairedSectors { get; init; }
        public double PercentComplete { get; init; }
        public TimeSpan Elapsed { get; init; }
        public TimeSpan? Eta { get; init; }
        public int CurrentBlockSizeBytes { get; init; }
        public bool Resumed { get; init; }
    }

    public sealed class Options
    {
        public long StartLba { get; init; }
        public long EndLba { get; init; }
        public int BlockSizeBytes { get; init; } = 4 * 1024 * 1024;
        public int BucketCount { get; init; } = 1000;
        public RepairMode RepairMode { get; init; } = RepairMode.Ignore;
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
                    if (speed > bestSpeed) { bestSpeed = speed; bestBlockSize = bs; }
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
        finally { handle.Dispose(); }

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
        TestState? resumeFrom,
        Action<ProgressSnapshot>? onProgress,
        Action<TestState>? onStateCheckpoint,
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
                RepairMode = opts.RepairMode,
            };
        }

        int bucketCount = (int)Math.Min(opts.BucketCount, Math.Max(1, blocksTotal));
        long bytesPerBucket = (totalBytes + bucketCount - 1) / bucketCount;
        bytesPerBucket = ((bytesPerBucket + opts.BlockSizeBytes - 1) / opts.BlockSizeBytes) * opts.BlockSizeBytes;
        bucketCount = (int)Math.Min(bucketCount, Math.Max(1, (totalBytes + bytesPerBucket - 1) / bytesPerBucket));

        BucketStats[] buckets;
        bool resumed = resumeFrom != null;

        if (resumed && resumeFrom!.Buckets.Count == bucketCount)
        {
            buckets = resumeFrom.Buckets.ToArray();
        }
        else
        {
            buckets = new BucketStats[bucketCount];
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
        }

        long bytesRead = resumed ? resumeFrom!.BytesReadBeforeResume : 0;
        long blocksRead = resumed ? resumeFrom!.BlocksReadBeforeResume : 0;
        var badBlocks = resumed ? new List<BadBlock>(resumeFrom!.BadBlocks) : new List<BadBlock>();
        int totalBadSectors = badBlocks.Sum(b => b.SectorCount);
        int repairedSectors = 0;
        int failedRepairs = 0;

        long resumeStartLba = resumed ? (resumeFrom!.LastCompletedLba + 1) : opts.StartLba;
        if (resumeStartLba > opts.EndLba) resumeStartLba = opts.EndLba + 1;

        long firstBlockIndex = (resumeStartLba - opts.StartLba) * (long)sectorSize / opts.BlockSizeBytes;

        long resumedByteOffset = (resumeStartLba - opts.StartLba) * (long)sectorSize;
        int currentBucketIdx = (int)Math.Min(bucketCount - 1, resumedByteOffset / bytesPerBucket);
        long bytesIntoCurrentBucket = resumedByteOffset - (long)currentBucketIdx * bytesPerBucket;
        if (bytesIntoCurrentBucket < 0) bytesIntoCurrentBucket = 0;

        var sw = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.UtcNow;
        long lastReportMs = 0;
        long lastReportBytes = bytesRead;
        long lastSaveMs = 0;

        // Use write handle if repair mode requires writing
        bool needWrite = opts.RepairMode != RepairMode.Ignore;
        var handle = needWrite ? NativeMethods.OpenDeviceForWrite(diskNumber) : NativeMethods.OpenDeviceForRead(diskNumber);

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
                RepairMode = opts.RepairMode,
                Status = "error",
                Error = "CreateFile failed, Win32Error=" + err + " (" + DescribeWin32(err) + ")",
            };
        }

        IntPtr buffer = IntPtr.Zero;
        try
        {
            long startOffset = resumeStartLba * (long)sectorSize;
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
                    RepairMode = opts.RepairMode,
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
                    RepairMode = opts.RepairMode,
                    Status = "error",
                    Error = "VirtualAlloc failed, Win32Error=" + err,
                };
            }

            long lastCompletedLba = resumed ? resumeFrom!.LastCompletedLba : (opts.StartLba - 1);

            for (long i = firstBlockIndex; i < blocksTotal; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    if (onStateCheckpoint != null)
                    {
                        onStateCheckpoint(BuildState(diskNumber, model, serial, logicalSectorSize, opts,
                            lastCompletedLba, sw.Elapsed.TotalSeconds + (resumed ? resumeFrom!.ElapsedSecondsBeforeResume : 0),
                            bytesRead, blocksRead, badBlocks, buckets));
                    }
                    break;
                }

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

                    // ---- Repair attempt ----
                    if (opts.RepairMode != RepairMode.Ignore)
                    {
                        bool repairOk = false;
                        try
                        {
                            switch (opts.RepairMode)
                            {
                                case RepairMode.Refresh:
                                    // If we read anything into buffer (short read), write it back; 
                                    // otherwise write zeros for the whole block
                                    if (readBytes > 0 && (readBytes % sectorSize) == 0)
                                    {
                                        byte[] partial = new byte[readBytes];
                                        Marshal.Copy(buffer, partial, 0, (int)readBytes);
                                        repairOk = RawDiskWriter.WriteAt(handle, blockOffset, partial);
                                    }
                                    else
                                    {
                                        repairOk = RawDiskWriter.WriteZerosAt(handle, blockOffset, opts.BlockSizeBytes);
                                    }
                                    break;

                                case RepairMode.Remap:
                                case RepairMode.Erase:
                                    repairOk = RawDiskWriter.WriteZerosAt(handle, blockOffset, opts.BlockSizeBytes);
                                    break;
                            }
                        }
                        catch
                        {
                            repairOk = false;
                        }

                        if (repairOk)
                        {
                            repairedSectors += sectors;
                            // Verify: re-read the block
                            if (NativeMethods.SetFilePointerEx(handle, blockOffset, out _, NativeMethods.FILE_BEGIN) &&
                                NativeMethods.ReadFile(handle, buffer, (uint)opts.BlockSizeBytes, out uint verifyRead, IntPtr.Zero) &&
                                verifyRead == (uint)opts.BlockSizeBytes)
                            {
                                // Verified OK
                            }
                        }
                        else
                        {
                            failedRepairs += sectors;
                        }
                    }

                    if (!NativeMethods.SetFilePointerEx(handle, (blockLbaEnd + 1) * (long)sectorSize, out _, NativeMethods.FILE_BEGIN))
                        break;

                    lastCompletedLba = blockLbaEnd;
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
                    lastCompletedLba = opts.StartLba + (blockOffset / (long)sectorSize) + (opts.BlockSizeBytes / (long)sectorSize) - 1;
                }

                blocksRead++;

                long nowMs = sw.ElapsedMilliseconds;
                if (onProgress != null && (nowMs - lastReportMs) >= 250)
                {
                    double nowSec = (nowMs / 1000.0) + (resumed ? resumeFrom!.ElapsedSecondsBeforeResume : 0);
                    double deltaSec = (nowMs - lastReportMs) / 1000.0;
                    double deltaBytes = bytesRead - lastReportBytes;
                    double instantMbps = deltaSec > 0 ? (deltaBytes / 1_048_576.0) / deltaSec : 0;
                    double avgMbps = nowSec > 0 ? (bytesRead / 1_048_576.0) / nowSec : 0;

                    double doneBytes = (resumeStartLba - opts.StartLba) * (long)sectorSize + (bytesRead - (resumed ? resumeFrom!.BytesReadBeforeResume : 0));
                    double pct = totalBytes > 0 ? (100.0 * doneBytes / totalBytes) : 0;
                    if (pct > 100) pct = 100;

                    TimeSpan? eta = null;
                    if (avgMbps > 0 && pct < 100)
                    {
                        double remainBytes = totalBytes - doneBytes;
                        double remainSec = (remainBytes / 1_048_576.0) / avgMbps;
                        eta = TimeSpan.FromSeconds(remainSec);
                    }

                    onProgress(new ProgressSnapshot
                    {
                        BytesRead = bytesRead,
                        BytesTotal = totalBytes,
                        InstantSpeedMbps = instantMbps,
                        AverageSpeedMbps = avgMbps,
                        BadSectors = totalBadSectors,
                        RepairedSectors = repairedSectors,
                        PercentComplete = pct,
                        Elapsed = TimeSpan.FromSeconds(nowSec),
                        Eta = eta,
                        CurrentBlockSizeBytes = opts.BlockSizeBytes,
                        Resumed = resumed,
                    });

                    lastReportMs = nowMs;
                    lastReportBytes = bytesRead;
                }

                if (onStateCheckpoint != null && (nowMs - lastSaveMs) >= 5000)
                {
                    onStateCheckpoint(BuildState(diskNumber, model, serial, logicalSectorSize, opts,
                        lastCompletedLba, sw.Elapsed.TotalSeconds + (resumed ? resumeFrom!.ElapsedSecondsBeforeResume : 0),
                        bytesRead, blocksRead, badBlocks, buckets));
                    lastSaveMs = nowMs;
                }
            }

            double totalElapsedSec = sw.Elapsed.TotalSeconds + (resumed ? resumeFrom!.ElapsedSecondsBeforeResume : 0);

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
                RepairMode = opts.RepairMode,
                RepairedSectors = repairedSectors,
                FailedRepairs = failedRepairs,
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
            if (buffer != IntPtr.Zero) NativeMethods.VirtualFree(buffer, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
            handle.Dispose();
        }
    }

    private static TestState BuildState(
        int diskNumber, string? model, string? serial, uint? logicalSectorSize,
        Options opts, long lastCompletedLba, double elapsedSeconds,
        long bytesRead, long blocksRead,
        List<BadBlock> badBlocks, BucketStats[] buckets)
    {
        return new TestState
        {
            DiskNumber = diskNumber,
            Model = model,
            SerialNumber = serial,
            LogicalSectorSize = logicalSectorSize,
            StartLba = opts.StartLba,
            EndLba = opts.EndLba,
            BlockSizeBytes = opts.BlockSizeBytes,
            BucketCount = opts.BucketCount,
            LastCompletedLba = lastCompletedLba,
            LastSavedAt = DateTimeOffset.UtcNow,
            ElapsedSecondsBeforeResume = elapsedSeconds,
            BytesReadBeforeResume = bytesRead,
            BlocksReadBeforeResume = blocksRead,
            BadBlocks = badBlocks.ToList(),
            Buckets = buckets.ToList(),
        };
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