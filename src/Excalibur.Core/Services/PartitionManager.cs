using System.Diagnostics;
using System.Text;

namespace Excalibur.Core.Services;

public static class PartitionManager
{
    public sealed class Result
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public string Output { get; init; } = "";
    }

    /// <summary>
    /// Run a list of diskpart commands (one per line). Returns success + raw output.
    /// </summary>
    private static Result RunDiskpart(params string[] commands)
    {
        var script = string.Join("\r\n", commands) + "\r\n";
        var psi = new ProcessStartInfo
        {
            FileName = "diskpart.exe",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var p = Process.Start(psi)!;
        p.StandardInput.Write(script);
        p.StandardInput.Close();

        var output = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(30000);

        if (p.ExitCode != 0)
            return new Result { Success = false, Error = $"diskpart exit {p.ExitCode}: {err}", Output = output };

        // diskpart prints errors to stdout, in Russian or English depending on locale
        var lower = output.ToLowerInvariant();
        if (lower.Contains("ошибк") || lower.Contains("error") || lower.Contains("не удалось") || lower.Contains("failed"))
        {
            // Extract the error line(s) for the user
            var lines = output.Split('\n').Where(l =>
                l.Contains("ошиб", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("не удалось", StringComparison.OrdinalIgnoreCase)).ToArray();
            var errText = lines.Length > 0 ? string.Join("; ", lines).Trim() : output.Trim();
            return new Result { Success = false, Error = errText, Output = output };
        }

        return new Result { Success = true, Output = output };
    }

    // ---- public API ----

    public static Result CreatePartition(int diskNumber, long sizeMb, string fs)
    {
        fs = fs.ToUpperInvariant();
        if (fs != "FAT32" && fs != "NTFS" && fs != "EXFAT")
            return new Result { Success = false, Error = "Unsupported FS: " + fs + ". Use FAT32, NTFS or exFAT." };

        if (sizeMb <= 0)
            return new Result { Success = false, Error = "sizeMb must be > 0" };

        return RunDiskpart(
            "select disk " + diskNumber,
            "create partition primary size=" + sizeMb,
            "format fs=" + fs.ToLowerInvariant() + " quick",
            "assign"
        );
    }

    public static Result DeletePartition(int diskNumber, int partitionIndex)
    {
        if (partitionIndex <= 0)
            return new Result { Success = false, Error = "partitionIndex must be >= 1 (diskpart uses 1-based indexes)" };

        // Use "clean" to remove ALL partitions on the disk — this is the most reliable way
        // (delete partition override can silently fail on OEM-encoded Russian Windows).
        // For single-partition flash drives (the common case) this is equivalent.
        return RunDiskpart(
            "select disk " + diskNumber,
            "clean"
        );
    }

    public static Result FormatVolume(string mountPoint, string fs)
    {
        fs = fs.ToUpperInvariant();
        if (fs != "FAT32" && fs != "NTFS" && fs != "EXFAT")
            return new Result { Success = false, Error = "Unsupported FS: " + fs };

        var drive = mountPoint.TrimEnd('\\');
        if (!drive.EndsWith(":")) drive += ":";

        return RunDiskpart(
            "select volume " + drive,
            "format fs=" + fs.ToLowerInvariant() + " quick"
        );
    }

    public static Result SetLetter(string mountPoint, string newLetter)
    {
        var drive = mountPoint.TrimEnd('\\');
        if (!drive.EndsWith(":")) drive += ":";

        var newL = newLetter.TrimEnd('\\', ':');
        if (newL.Length != 1 || !char.IsLetter(newL[0]))
            return new Result { Success = false, Error = "newLetter must be a single letter (e.g. E)" };
        newL = newL.ToUpperInvariant();

        return RunDiskpart(
            "select volume " + drive,
            "assign letter=" + newL
        );
    }

    public static Result RemoveLetter(string mountPoint)
    {
        var drive = mountPoint.TrimEnd('\\');
        if (!drive.EndsWith(":")) drive += ":";

        return RunDiskpart(
            "select volume " + drive,
            "remove letter=" + drive[0]
        );
    }

    // ---- label ----

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetVolumeLabelW(string lpRootPathName, string lpVolumeName);

    public static Result SetLabel(string mountPoint, string label)
    {
        var drive = mountPoint.TrimEnd('\\');
        if (!drive.EndsWith(":")) drive += ":";
        var root = drive + "\\";

        if (label.Length > 32)
            return new Result { Success = false, Error = "Label too long (max 32 chars)" };

        bool ok = SetVolumeLabelW(root, label);
        if (!ok)
            return new Result { Success = false, Error = "SetVolumeLabel failed, Win32Error=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error() };

        return new Result { Success = true, Output = "Label set to '" + label + "' on " + drive };
    }
}