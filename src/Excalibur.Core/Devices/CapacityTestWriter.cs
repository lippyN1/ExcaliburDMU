using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Excalibur.Core.Models;
using Excalibur.Core.Native;

namespace Excalibur.Core.Devices;

public sealed class CapacityTestWriter
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
        IntPtr lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    public sealed class ProgressEvent
    {
        public string Phase { get; init; } = "";
        public int CurrentMarker { get; init; }
        public int TotalMarkers { get; init; }
        public long BytesProcessed { get; init; }
        public long BytesTotal { get; init; }
        public double PercentComplete { get; init; }
        public double SpeedMbps { get; init; }
        public string? Message { get; init; }
    }

    public CapacityTestResult Run(
        PhysicalDisk disk,
        bool fullScan,
        int markerSizeBytes,
        bool preservePartitionTable,
        Action<ProgressEvent>? onProgress,
        CancellationToken ct)
    {
        if (markerSizeBytes <= 0 || markerSizeBytes % 4096 != 0)
            throw new ArgumentException("markerSizeBytes must be positive multiple of 4096");

        long totalBytes = disk.CapacityBytes ?? 0;
        if (totalBytes <= 0) throw new InvalidOperationException("Disk capacity unknown");

        long sectorSize = disk.LogicalSectorSize ?? 512;
        if (sectorSize == 0) sectorSize = 512;

        var warnings = new List<string>();
        var markers = new List<CapacityMarker>();

        long[] positions = {
            0L,
            totalBytes / 4,
            totalBytes / 2,
            3 * totalBytes / 4,
            totalBytes - markerSizeBytes
        };
        positions = positions.Distinct().Where(p => p >= 0 && p + markerSizeBytes <= totalBytes).OrderBy(p => p).ToArray();

        int markerCount = positions.Length;
        var swTotal = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.UtcNow;

        var handle = NativeMethods.CreateFileW(
            $@"\\.\PhysicalDrive{disk.DiskNumber}",
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_NO_BUFFERING | NativeMethods.FILE_FLAG_WRITE_THROUGH,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            return new CapacityTestResult
            {
                DiskNumber = disk.DiskNumber, Model = disk.Model, SerialNumber = disk.SerialNumber,
                DeclaredCapacityBytes = totalBytes, ActualCapacityBytes = 0,
                LogicalSectorSize = disk.LogicalSectorSize, FullScan = fullScan,
                MarkerSizeBytes = markerSizeBytes, MarkerCount = markerCount,
                StartedAt = startedAt, FinishedAt = DateTimeOffset.UtcNow,
                Status = "error", Error = "CreateFile failed, Win32Error=" + err,
                Verdict = "Unknown",
            };
        }

        DiskLayoutBackup? layoutBackup = null;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (preservePartitionTable)
            {
                try { layoutBackup = PartitionTableBackup.Capture(handle, disk.DiskNumber, sectorSize); }
                catch (Exception ex) { warnings.Add("Could not backup partition table: " + ex.Message); }
            }

            buffer = NativeMethods.VirtualAlloc(IntPtr.Zero, (UIntPtr)markerSizeBytes,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
            if (buffer == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                return new CapacityTestResult
                {
                    DiskNumber = disk.DiskNumber, Model = disk.Model, SerialNumber = disk.SerialNumber,
                    DeclaredCapacityBytes = totalBytes, LogicalSectorSize = disk.LogicalSectorSize,
                    FullScan = fullScan, MarkerSizeBytes = markerSizeBytes, MarkerCount = markerCount,
                    StartedAt = startedAt, FinishedAt = DateTimeOffset.UtcNow,
                    Status = "error", Error = "VirtualAlloc failed, Win32Error=" + err,
                    Verdict = "Unknown",
                };
            }

            // ---- WRITE PHASE ----
            double totalWriteSec = 0;
            long totalWritten = 0;

            for (int m = 0; m < markerCount; m++)
            {
                if (ct.IsCancellationRequested) break;

                long position = positions[m];
                FillBufferWithPattern(buffer, markerSizeBytes, disk.SerialNumber ?? "unknown", m);

                onProgress?.Invoke(new ProgressEvent
                {
                    Phase = "write", CurrentMarker = m + 1, TotalMarkers = markerCount,
                    BytesProcessed = position, BytesTotal = totalBytes,
                    PercentComplete = 100.0 * (m + 1) / markerCount,
                    Message = "Writing marker at byte " + position,
                });

                var sw = Stopwatch.StartNew();
                bool ok = SeekAndWrite(handle, position, buffer, markerSizeBytes, out long writtenBytes, out string writeErr);
                sw.Stop();
                totalWriteSec += sw.Elapsed.TotalSeconds;
                totalWritten += writtenBytes;

                markers.Add(new CapacityMarker
                {
                    Index = m, Lba = position / sectorSize, ByteOffset = position,
                    SizeBytes = markerSizeBytes, Hash = ComputeBufferHash(buffer, markerSizeBytes),
                    WriteOk = ok, Error = ok ? null : writeErr,
                });

                if (!ok) warnings.Add("Write failed at marker " + m + " (" + writeErr + ")");
            }

            // ---- READ PHASE ----
            double totalReadSec = 0;
            long totalRead = 0;

            for (int m = 0; m < markers.Count; m++)
            {
                if (ct.IsCancellationRequested) break;
                var marker = markers[m];
                if (!marker.WriteOk) continue;

                onProgress?.Invoke(new ProgressEvent
                {
                    Phase = "read", CurrentMarker = m + 1, TotalMarkers = markerCount,
                    BytesProcessed = marker.ByteOffset, BytesTotal = totalBytes,
                    PercentComplete = 100.0 * (m + 1) / markerCount,
                    Message = "Reading marker at byte " + marker.ByteOffset,
                });

                var sw = Stopwatch.StartNew();
                bool ok = SeekAndRead(handle, marker.ByteOffset, buffer, marker.SizeBytes, out long readBytes, out string readErr);
                sw.Stop();
                totalReadSec += sw.Elapsed.TotalSeconds;
                totalRead += readBytes;

                string readHash = ok && readBytes == marker.SizeBytes ? ComputeBufferHash(buffer, marker.SizeBytes) : "";

                markers[m] = new CapacityMarker
                {
                    Index = marker.Index, Lba = marker.Lba, ByteOffset = marker.ByteOffset,
                    SizeBytes = marker.SizeBytes, Hash = marker.Hash,
                    WriteOk = marker.WriteOk, ReadOk = ok,
                    HashMatches = ok && readHash == marker.Hash,
                    Error = ok ? null : readErr,
                };

                if (!ok) warnings.Add("Read failed at marker " + m + " (" + readErr + ")");
                else if (!markers[m].HashMatches) warnings.Add("Hash mismatch at marker " + m);
            }

            // ---- FULL SCAN ----
            long actualCapacity = totalBytes;
            bool fullScanFoundTruncation = false;

            if (fullScan && !ct.IsCancellationRequested)
            {
                long chunkSize = 4L * 1024 * 1024;
                IntPtr chunkBuf = NativeMethods.VirtualAlloc(IntPtr.Zero, (UIntPtr)chunkSize,
                    NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
                if (chunkBuf != IntPtr.Zero)
                {
                    try
                    {
                        long totalScanned = 0;
                        long chunkIndex = 0;
                        bool seenMismatch = false;
                        for (long pos = 0; pos + chunkSize <= totalBytes; pos += chunkSize, chunkIndex++)
                        {
                            if (ct.IsCancellationRequested) break;

                            FillBufferWithPattern(chunkBuf, (int)chunkSize, disk.SerialNumber ?? "unknown",
                                1000 + (int)(chunkIndex & 0x7FFF));
                            string expectedHash = ComputeBufferHash(chunkBuf, (int)chunkSize);

                            if (!SeekAndWrite(handle, pos, chunkBuf, (int)chunkSize, out _, out _))
                            {
                                actualCapacity = pos;
                                fullScanFoundTruncation = true;
                                break;
                            }
                            FillBufferWithPattern(chunkBuf, (int)chunkSize, disk.SerialNumber ?? "unknown",
                                1000 + (int)(chunkIndex & 0x7FFF));
                            if (!SeekAndRead(handle, pos, chunkBuf, (int)chunkSize, out long rd, out _))
                            {
                                actualCapacity = pos;
                                fullScanFoundTruncation = true;
                                break;
                            }
                            if (rd != chunkSize)
                            {
                                actualCapacity = pos;
                                fullScanFoundTruncation = true;
                                break;
                            }
                            string readHash = ComputeBufferHash(chunkBuf, (int)chunkSize);
                            if (readHash != expectedHash)
                            {
                                actualCapacity = pos;
                                fullScanFoundTruncation = true;
                                seenMismatch = true;
                                break;
                            }

                            totalScanned += chunkSize;

                            if (chunkIndex % 50 == 0 || pos + chunkSize * 2 > totalBytes)
                            {
                                onProgress?.Invoke(new ProgressEvent
                                {
                                    Phase = "full-scan", CurrentMarker = 0, TotalMarkers = 0,
                                    BytesProcessed = totalScanned, BytesTotal = totalBytes,
                                    PercentComplete = 100.0 * totalScanned / totalBytes,
                                    SpeedMbps = (totalScanned / 1_048_576.0) / Math.Max(swTotal.Elapsed.TotalSeconds, 0.001),
                                    Message = "Full scan at byte " + pos,
                                });
                            }
                        }
                        if (fullScanFoundTruncation)
                            warnings.Add("Full scan stopped at byte " + actualCapacity +
                                         (seenMismatch ? " (hash mismatch — fake capacity)" : " (write/read failed)"));
                    }
                    finally
                    {
                        NativeMethods.VirtualFree(chunkBuf, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
                    }
                }
            }

            swTotal.Stop();
            double totalSec = swTotal.Elapsed.TotalSeconds;

            // ---- Verdict ----
            bool anyWriteFailed = markers.Any(m => !m.WriteOk);
            bool anyReadFailed = markers.Any(m => m.WriteOk && !m.ReadOk);
            bool anyHashMismatch = markers.Any(m => m.ReadOk && !m.HashMatches);
            bool lastMarkerOk = markers.Count > 0 && markers[^1].WriteOk && markers[^1].ReadOk && markers[^1].HashMatches;

            string verdict;
            var reasons = new List<string>();

            if (anyWriteFailed) { verdict = "Failing"; reasons.Add("Write failed for one or more markers"); }
            else if (anyHashMismatch) { verdict = "Failing"; reasons.Add("Hash mismatch — written data does not match read-back data"); }
            else if (!lastMarkerOk) { verdict = "Failing"; reasons.Add("Last marker near declared end did not verify — capacity likely FAKE"); }
            else if (anyReadFailed) { verdict = "Warning"; reasons.Add("Some markers read failed"); }
            else { verdict = "Healthy"; reasons.Add("All markers written, read back, and hash-verified successfully"); }

            if (fullScanFoundTruncation)
            {
                verdict = "Failing";
                reasons.Add("Full scan truncated at byte " + actualCapacity + " — actual capacity is smaller than declared");
            }

            double writeSpeed = totalWriteSec > 0 ? (totalWritten / 1_048_576.0) / totalWriteSec : 0;
            double readSpeed = totalReadSec > 0 ? (totalRead / 1_048_576.0) / totalReadSec : 0;

            return new CapacityTestResult
            {
                DiskNumber = disk.DiskNumber, Model = disk.Model, SerialNumber = disk.SerialNumber,
                DeclaredCapacityBytes = totalBytes,
                ActualCapacityBytes = fullScan ? actualCapacity : totalBytes,
                LogicalSectorSize = disk.LogicalSectorSize,
                FullScan = fullScan, MarkerSizeBytes = markerSizeBytes, MarkerCount = markerCount,
                WriteSpeedMbps = writeSpeed, ReadSpeedMbps = readSpeed,
                StartedAt = startedAt, FinishedAt = DateTimeOffset.UtcNow, ElapsedSeconds = totalSec,
                Markers = markers, Warnings = warnings,
                Verdict = verdict, VerdictReasons = reasons,
                Status = ct.IsCancellationRequested ? "cancelled" : "ok",
            };
        }
        finally
        {
            if (layoutBackup != null)
            {
                try { PartitionTableBackup.Restore(handle, layoutBackup); }
                catch (Exception ex) { warnings.Add("Partition table restore failed: " + ex.Message); }
            }

            if (buffer != IntPtr.Zero) NativeMethods.VirtualFree(buffer, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
            handle.Dispose();
        }
    }

    private static void FillBufferWithPattern(IntPtr buf, int size, string seed, int markerIndex)
    {
        const int blockSize = 32;
        int counter = 0;
        for (int offset = 0; offset < size; offset += blockSize, counter++)
        {
            var input = System.Text.Encoding.UTF8.GetBytes(seed + "|" + markerIndex + "|" + counter);
            var hash = SHA256.HashData(input);
            int toCopy = Math.Min(blockSize, size - offset);
            Marshal.Copy(hash, 0, IntPtr.Add(buf, offset), toCopy);
        }
    }

    private static string ComputeBufferHash(IntPtr buf, int size)
    {
        byte[] managed = new byte[size];
        Marshal.Copy(buf, managed, 0, size);
        return Convert.ToHexString(SHA256.HashData(managed)).ToLowerInvariant();
    }

    private static bool SeekAndWrite(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long byteOffset, IntPtr buf, int size, out long written, out string error)
    {
        written = 0; error = "";
        if (!NativeMethods.SetFilePointerEx(handle, byteOffset, out _, NativeMethods.FILE_BEGIN))
        { error = "SetFilePointerEx failed, Win32Error=" + Marshal.GetLastWin32Error(); return false; }
        if (!WriteFile(handle, buf, (uint)size, out uint bw, IntPtr.Zero) || bw != (uint)size)
        { error = "WriteFile failed, Win32Error=" + Marshal.GetLastWin32Error(); return false; }
        written = bw; return true;
    }

    private static bool SeekAndRead(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long byteOffset, IntPtr buf, int size, out long read, out string error)
    {
        read = 0; error = "";
        if (!NativeMethods.SetFilePointerEx(handle, byteOffset, out _, NativeMethods.FILE_BEGIN))
        { error = "SetFilePointerEx failed, Win32Error=" + Marshal.GetLastWin32Error(); return false; }
        if (!NativeMethods.ReadFile(handle, buf, (uint)size, out uint br, IntPtr.Zero) || br != (uint)size)
        { error = "ReadFile failed, Win32Error=" + Marshal.GetLastWin32Error(); return false; }
        read = br; return true;
    }
}